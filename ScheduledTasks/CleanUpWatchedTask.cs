using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Jellyfin.Plugin.DialogueBoost.ScheduledTasks;

public class CleanUpWatchedTask : IScheduledTask
{
    private readonly IMediaLibrary _library;
    private readonly WatchedItems _watched;
    private readonly ProcessingStateRepository _stateRepository;
    private readonly IMetadataRefresher _refresher;
    private readonly SidecarPlacement _placement;
    private readonly SelectionStore _selectionStore;
    private readonly ScopeResolver _scopeResolver;
    private readonly CleanupRunOverride _runOverride;
    private readonly ILogger<CleanUpWatchedTask> _logger;

    public string Name => "Dialogue Boost: Cleanup Watched Audio Files";

    public string Key => PluginTasks.CleanupKey;

    public string Description => "Scans libraries for watched items and deletes Dialogue Boost generated sidecar audio files to free up disk space.";

    public string Category => "Dialogue Boost";

    public CleanUpWatchedTask(
        IMediaLibrary library,
        WatchedItems watched,
        ProcessingStateRepository stateRepository,
        IMetadataRefresher refresher,
        SidecarPlacement placement,
        SelectionStore selectionStore,
        ScopeResolver scopeResolver,
        CleanupRunOverride runOverride,
        ILogger<CleanUpWatchedTask> logger)
    {
        _library = library;
        _watched = watched;
        _stateRepository = stateRepository;
        _refresher = refresher;
        _placement = placement;
        _selectionStore = selectionStore;
        _scopeResolver = scopeResolver;
        _runOverride = runOverride;
        _logger = logger;
    }

    /// <summary>
    /// No default schedule: this task deletes sidecars, so it runs only when somebody has asked for
    /// it — from the config page, or from Jellyfin's dashboard. Both write the same trigger.
    /// </summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Enumerable.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.Enabled)
        {
            _logger.LogInformation("DialogueBoost is disabled. Watched audio cleanup task cancelled.");
            return;
        }

        // Taken, not peeked: the instruction is spent by this run whether or not the setting
        // already said the same thing.
        bool includeExemptThisRun = _runOverride.TakeIncludeExempt();
        bool includeExempt = config.IncludeExemptInCleanup || includeExemptThisRun;

        _logger.LogInformation(
            "Starting DialogueBoost watched audio files cleanup task.{Exempt:l}",
            includeExemptThisRun ? " Sidecars marked exempt are included in this run only." : string.Empty);

        if (!config.SkipWatchedItems)
        {
            // The two settings are each reasonable and together they are a treadmill: this task
            // deletes the track for a watched item, and the next normalization run writes it back
            // because nothing tells it not to. Said once per run rather than guessed at — somebody
            // may want exactly that.
            _logger.LogWarning(
                "DialogueBoost: 'Skip items already watched' is off while the cleanup runs, so every "
                + "track this deletes will be written again by the next normalization run.");
        }

        // 1. Collect candidate records from SQLite repository
        var records = await _stateRepository.GetRecordsWithSidecarsAsync().ConfigureAwait(false);

        // Group records by ItemId
        var recordsByItem = records
            .GroupBy(r => r.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 2. Collect candidate items from the selected scopes as well
        var selection = await _selectionStore.GetAsync(cancellationToken).ConfigureAwait(false);
        var libraryItems = _scopeResolver.ResolveItems(selection);

        // Combine item IDs from DB records and library scan
        var itemIdsToInspect = new HashSet<string>(recordsByItem.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var libItem in libraryItems)
        {
            itemIdsToInspect.Add(libItem.Id.ToString("N"));
        }

        var itemList = itemIdsToInspect.ToList();
        if (itemList.Count == 0)
        {
            _logger.LogInformation("No items found to inspect for watched audio cleanup.");
            return;
        }

        int deletedSidecarsCount = 0;
        long totalBytesFreed = 0;
        var refreshed = new List<BaseItem>();
        var profiles = config.GetAllProfiles().ToList();

        for (int i = 0; i < itemList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string itemIdStr = itemList[i];

            double pct = ((double)i / itemList.Count) * 100.0;
            progress.Report(pct);

            if (!Guid.TryParse(itemIdStr, out Guid itemGuid))
            {
                continue;
            }

            var baseItem = _library.ById(itemGuid);
            if (baseItem == null)
            {
                // Item removed from Jellyfin: clean up lingering sidecar file if specified in record
                if (recordsByItem.TryGetValue(itemIdStr, out var orphanRecords))
                {
                    foreach (var orphanRec in orphanRecords)
                    {
                        if (!string.IsNullOrWhiteSpace(orphanRec.SidecarPath) && File.Exists(orphanRec.SidecarPath))
                        {
                            try
                            {
                                long size = new FileInfo(orphanRec.SidecarPath).Length;
                                if (config.DryRun)
                                {
                                    _logger.LogInformation("[DryRun] Would delete orphaned sidecar file: {Path}", orphanRec.SidecarPath);
                                }
                                else
                                {
                                    File.Delete(orphanRec.SidecarPath);
                                    _logger.LogInformation("Deleted orphaned sidecar file: {Path}", orphanRec.SidecarPath);
                                }
                                totalBytesFreed += size;
                                deletedSidecarsCount++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete orphaned sidecar file at {Path}", orphanRec.SidecarPath);
                            }
                        }
                    }
                    if (!config.DryRun)
                    {
                        await _stateRepository.DeleteAllRecordsForItemAsync(itemIdStr).ConfigureAwait(false);
                    }
                }
                continue;
            }

            // Check if baseItem is marked as watched
            if (!_watched.IsWatched(baseItem, config))
            {
                continue;
            }

            recordsByItem.TryGetValue(itemIdStr, out var itemRecords);

            // Check if sidecar for this item is exempt from automated cleanup (manually created)
            if (!includeExempt && itemRecords != null && itemRecords.Any(r => r.ExemptFromCleanup))
            {
                _logger.LogInformation("Item '{ItemName:l}' is watched but marked as exempt from cleanup (manually created). Skipping cleanup.", baseItem.Name);
                continue;
            }

            // Item IS watched: remove sidecars and DB records
            bool itemModified = false;
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Delete sidecars listed in DB records
            if (itemRecords != null)
            {
                foreach (var rec in itemRecords)
                {
                    if (!string.IsNullOrWhiteSpace(rec.SidecarPath) && File.Exists(rec.SidecarPath))
                    {
                        processedPaths.Add(rec.SidecarPath);
                        try
                        {
                            long size = new FileInfo(rec.SidecarPath).Length;
                            if (config.DryRun)
                            {
                                _logger.LogInformation("[DryRun] Would delete sidecar audio file for watched item '{ItemName:l}': {Path} ({SizeMB:l} MB freed)",
                                    baseItem.Name, rec.SidecarPath, (size / (1024.0 * 1024.0)).ToString("F1"));
                            }
                            else
                            {
                                File.Delete(rec.SidecarPath);
                                _logger.LogInformation("Deleted sidecar audio file for watched item '{ItemName:l}': {Path} ({SizeMB:l} MB freed)",
                                    baseItem.Name, rec.SidecarPath, (size / (1024.0 * 1024.0)).ToString("F1"));
                            }
                            totalBytesFreed += size;
                            deletedSidecarsCount++;
                            itemModified = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed deleting sidecar file at {Path}", rec.SidecarPath);
                        }
                    }
                }
            }

            // Also check on-disk sidecar paths for all enabled profiles (in case DB record is missing)
            if (!string.IsNullOrWhiteSpace(baseItem.Path))
            {
                // Both names a profile could have written — the one it writes today and the one it
                // wrote while it was the profile playback started on — in both folders a track can
                // be in. A file left under the other name, or the other setting, is a track in the
                // audio menu that nothing would ever collect.
                foreach (var computedSidecarPath in profiles.SelectMany(
                             prof => _placement.CandidatePaths(baseItem, prof.SidecarNamingMarker)))
                {
                    if (!processedPaths.Contains(computedSidecarPath) && File.Exists(computedSidecarPath))
                    {
                        processedPaths.Add(computedSidecarPath);
                        try
                        {
                            long size = new FileInfo(computedSidecarPath).Length;
                            if (config.DryRun)
                            {
                                _logger.LogInformation("[DryRun] Would delete sidecar audio file for watched item '{ItemName:l}': {Path} ({SizeMB:l} MB freed)",
                                    baseItem.Name, computedSidecarPath, (size / (1024.0 * 1024.0)).ToString("F1"));
                            }
                            else
                            {
                                File.Delete(computedSidecarPath);
                                _logger.LogInformation("Deleted sidecar audio file for watched item '{ItemName:l}': {Path} ({SizeMB:l} MB freed)",
                                    baseItem.Name, computedSidecarPath, (size / (1024.0 * 1024.0)).ToString("F1"));
                            }
                            totalBytesFreed += size;
                            deletedSidecarsCount++;
                            itemModified = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed deleting sidecar file at {Path}", computedSidecarPath);
                        }
                    }
                }
            }

            // Clean database state records for this item (only if not DryRun)
            if (!config.DryRun)
            {
                await _stateRepository.DeleteAllRecordsForItemAsync(itemIdStr).ConfigureAwait(false);
            }

            if (itemModified && !config.DryRun)
            {
                refreshed.Add(baseItem);
            }
        }

        progress.Report(100.0);

        if (refreshed.Count > 0 && !config.DryRun)
        {
            // A deleted sidecar is still in the audio menu until Jellyfin reads the folder again —
            // and this is the burst that produced `Guid can't be empty` from Jellyfin's own refresh
            // queue on a live server, which
            // is why the batch is ours to run rather than Jellyfin's queue's.
            await _refresher.RefreshBatchAsync(refreshed, cancellationToken).ConfigureAwait(false);
        }

        double freedMb = totalBytesFreed / (1024.0 * 1024.0);
        if (config.DryRun)
        {
            _logger.LogInformation("[DryRun] DialogueBoost watched audio cleanup finished. Would delete sidecars: {Count}, Total space that would be freed: {FreedMB:l} MB.",
                deletedSidecarsCount, freedMb.ToString("F1"));
        }
        else
        {
            _logger.LogInformation("DialogueBoost watched audio cleanup finished. Deleted sidecars: {Count}, Total space freed: {FreedMB:l} MB.",
                deletedSidecarsCount, freedMb.ToString("F1"));
        }
    }
}
