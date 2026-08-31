using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Jellyfin.Plugin.DialogueBoost.ScheduledTasks;

public class NormalizeLibraryTask : IScheduledTask
{
    private readonly ItemProcessor _itemProcessor;
    private readonly IMetadataRefresher _refresher;
    private readonly SelectionStore _selectionStore;
    private readonly ScopeResolver _scopeResolver;
    private readonly NewLibraries _newLibraries;
    private readonly ILogger<NormalizeLibraryTask> _logger;

    public string Name => "Dialogue Boost: Library Normalization";

    public string Key => PluginTasks.NormalizeKey;

    public string Description => "Scans selected libraries and generates dialogue-enhanced audio sidecars for media files.";

    public string Category => "Dialogue Boost";

    public NormalizeLibraryTask(
        ItemProcessor itemProcessor,
        IMetadataRefresher refresher,
        SelectionStore selectionStore,
        ScopeResolver scopeResolver,
        NewLibraries newLibraries,
        ILogger<NormalizeLibraryTask> logger)
    {
        _itemProcessor = itemProcessor;
        _refresher = refresher;
        _selectionStore = selectionStore;
        _scopeResolver = scopeResolver;
        _newLibraries = newLibraries;
        _logger = logger;
    }

    /// <summary>
    /// A fresh install runs nightly at 02:00. Jellyfin asks for defaults only while the task has no
    /// stored schedule, so this is a starting point and not a setting: covering media that arrives
    /// on its own needs a run to arrive at, and the page (or Jellyfin's dashboard) owns it from the
    /// first change onwards.
    /// </summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.Enabled)
        {
            _logger.LogInformation("DialogueBoost is disabled. Scheduled normalization task cancelled.");
            return;
        }

        var selection = await _selectionStore.GetAsync(cancellationToken).ConfigureAwait(false);

        // A library added to Jellyfin since the selection was saved is inside nothing, so no scope
        // can reach it — the policy is the only thing that decides whether this run covers it.
        selection = await _newLibraries
            .ApplyPolicyAsync(selection, config.NewLibraryPolicy, cancellationToken)
            .ConfigureAwait(false);

        if (selection.IsEmpty)
        {
            _logger.LogInformation("DialogueBoost: nothing is selected, so there is nothing to normalize.");
            return;
        }

        // Resolved from the library on every run, never from a stored member list — which is what
        // covers a download that landed since the scopes were chosen.
        var targetItems = _scopeResolver.ResolveItems(selection);

        if (targetItems.Count == 0)
        {
            _logger.LogInformation("DialogueBoost: the selected scopes contain no video items.");
            return;
        }

        _logger.LogInformation("Starting DialogueBoost normalization for {Count} items.", targetItems.Count);

        var written = new List<BaseItem>();
        var tally = new Dictionary<ProcessingOutcome, int>();

        int maxJobs = Math.Clamp(config.MaxConcurrentJobs, 1, 32);
        _logger.LogInformation("Processing items with MaxConcurrentJobs = {MaxJobs}", maxJobs);

        // At most one profile's sidecar may carry Jellyfin's ".default" filename token. Said once
        // here rather than once per item, because it is a property of the settings, not the file.
        var claiming = config.GetAllProfiles().Where(p => p.Enabled && p.SetAsDefaultTrack).ToList();
        if (claiming.Count > 1)
        {
            _logger.LogWarning(
                "{Count} profiles are set to start playback on their own track; only '{Winner:l}' will. " +
                "Jellyfin takes the lowest external stream index, so two claims is not a choice this plugin gets to make.",
                claiming.Count,
                claiming[0].Name);
        }

        int completedCount = 0;
        var lockObj = new object();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxJobs,
            CancellationToken = cancellationToken
        };

        try
        {
            await Parallel.ForEachAsync(targetItems, parallelOptions, async (item, ct) =>
            {
                var results = await _itemProcessor.ProcessItemAsync(item, specificProfileId: null, ct).ConfigureAwait(false);

                lock (lockObj)
                {
                    foreach (var result in results)
                    {
                        tally[result.Outcome] = tally.GetValueOrDefault(result.Outcome) + 1;

                        // Only a track written or moved now needs Jellyfin to look again. One that
                        // was already there was already visible, and refreshing it is work for no
                        // change.
                        if (result.ChangedFiles && !written.Contains(item))
                        {
                            written.Add(item);
                        }
                    }

                    completedCount++;
                    double pct = ((double)completedCount / targetItems.Count) * 100.0;
                    progress.Report(pct);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            // After the loop, never inside it: a refresh between two encodes was measurably slower for
            // no gain, and it is the batch that makes a run's writes visible in one pass.
            // Not conditional on the run finishing either: a track already on disk that Jellyfin has
            // not looked at is a track nobody can select, and a later run finds it already current
            // and refreshes nothing. So a stopped run still makes visible what it managed to write.
            await _refresher.RefreshBatchAsync(written, CancellationToken.None).ConfigureAwait(false);
        }

        progress.Report(100.0);

        _logger.LogInformation(
            "DialogueBoost normalization finished. Wrote {Written}, moved {Moved}, already current {AlreadyDone}, skipped {Skipped}, failed {Failed}.",
            tally.GetValueOrDefault(ProcessingOutcome.Written),
            tally.GetValueOrDefault(ProcessingOutcome.Moved),
            tally.GetValueOrDefault(ProcessingOutcome.AlreadyDone),
            tally.GetValueOrDefault(ProcessingOutcome.Skipped),
            tally.GetValueOrDefault(ProcessingOutcome.Failed));
    }
}
