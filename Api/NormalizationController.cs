using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DialogueBoost.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class NormalizationController : ControllerBase
{
    private readonly IMediaLibrary _library;
    private readonly ItemProcessor _itemProcessor;
    private readonly ProcessingStateRepository _stateRepository;
    private readonly IMetadataRefresher _refresher;
    private readonly SelectionStore _selectionStore;
    private readonly CleanupRunOverride _cleanupOverride;
    private readonly WorkForecaster _forecaster;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<NormalizationController> _logger;

    public NormalizationController(
        IMediaLibrary library,
        ItemProcessor itemProcessor,
        ProcessingStateRepository stateRepository,
        IMetadataRefresher refresher,
        SelectionStore selectionStore,
        CleanupRunOverride cleanupOverride,
        WorkForecaster forecaster,
        ITaskManager taskManager,
        ILogger<NormalizationController> logger)
    {
        _library = library;
        _itemProcessor = itemProcessor;
        _stateRepository = stateRepository;
        _refresher = refresher;
        _selectionStore = selectionStore;
        _cleanupOverride = cleanupOverride;
        _forecaster = forecaster;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// Triggers execution of the Dialogue Boost library normalization scheduled task.
    /// </summary>
    [HttpPost("/Plugins/DialogueBoost/Task/Start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult StartTask()
    {
        var taskWorker = PluginTasks.Find(_taskManager, PluginTasks.NormalizeKey);
        if (taskWorker != null)
        {
            _taskManager.Execute(taskWorker, new TaskOptions());
            return Ok(new { Message = "Scheduled task started." });
        }
        return NotFound("Dialogue Boost scheduled task not found.");
    }

    /// <summary>
    /// Cancels execution of the Dialogue Boost library normalization scheduled task.
    /// </summary>
    [HttpPost("/Plugins/DialogueBoost/Task/Stop")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult StopTask()
    {
        _taskManager.CancelIfRunning<ScheduledTasks.NormalizeLibraryTask>();
        return Ok(new { Message = "Scheduled task stop requested." });
    }

    /// <summary>
    /// Triggers execution of the Dialogue Boost watched files cleanup scheduled task.
    /// </summary>
    /// <param name="includeExempt">
    /// <c>true</c> to have this one run also delete sidecars marked exempt from cleanup. It applies
    /// to this run only and changes no setting — which is what "purge now" always meant.
    /// </param>
    [HttpPost("/Plugins/DialogueBoost/Task/CleanUpWatched/Start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartCleanUpWatchedTask([FromQuery] bool? includeExempt = null)
    {
        var taskWorker = PluginTasks.Find(_taskManager, PluginTasks.CleanupKey);
        if (taskWorker is null)
        {
            return NotFound("Dialogue Boost watched audio cleanup scheduled task not found.");
        }

        if (taskWorker.State != TaskState.Idle)
        {
            // Starting it again would do nothing, and a one-off instruction left behind would then
            // apply to whichever run came next.
            return Conflict(new { Message = "The watched audio cleanup task is already running." });
        }

        if (includeExempt == true)
        {
            _cleanupOverride.IncludeExemptOnNextRun();
        }

        _taskManager.Execute(taskWorker, new TaskOptions());
        return Ok(new { Message = "Watched audio cleanup task started." });
    }

    /// <summary>
    /// Cancels execution of the Dialogue Boost watched files cleanup scheduled task.
    /// </summary>
    [HttpPost("/Plugins/DialogueBoost/Task/CleanUpWatched/Stop")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult StopCleanUpWatchedTask()
    {
        _taskManager.CancelIfRunning<ScheduledTasks.CleanUpWatchedTask>();
        return Ok(new { Message = "Watched audio cleanup task stop requested." });
    }

    /// <summary>
    /// Gets real-time status and progress percentage for Dialogue Boost scheduled tasks.
    /// </summary>
    [HttpGet("/Plugins/DialogueBoost/Tasks/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetTasksStatus()
    {
        var tasks = _taskManager.ScheduledTasks
            .Where(t => t.ScheduledTask != null &&
                       (t.ScheduledTask.Key == PluginTasks.NormalizeKey || t.ScheduledTask.Key == PluginTasks.CleanupKey))
            .Select(t => new
            {
                Key = t.ScheduledTask!.Key,
                Name = t.ScheduledTask.Name,
                State = t.State.ToString(),
                CurrentProgressPercentage = t.CurrentProgress,
                LastExecutionResult = t.LastExecutionResult != null ? new
                {
                    Status = t.LastExecutionResult.Status.ToString(),
                    StartTimeUtc = t.LastExecutionResult.StartTimeUtc,
                    EndTimeUtc = t.LastExecutionResult.EndTimeUtc,
                    ErrorMessage = t.LastExecutionResult.ErrorMessage
                } : null
            })
            .ToList();

        return Ok(tasks);
    }

    /// <summary>
    /// Immediately processes a single item outside the schedule.
    /// </summary>
    [HttpPost("/Plugins/DialogueBoost/Items/{itemId}/Process")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ItemProcessingResult>>> ProcessItem(
        [FromRoute] Guid itemId,
        [FromQuery] string? profileId,
        [FromQuery] bool overrideWatched = true,
        CancellationToken cancellationToken = default)
    {
        var item = _library.ById(itemId);
        if (item == null)
        {
            return NotFound($"Item with ID {itemId} not found.");
        }

        var results = await _itemProcessor.ProcessItemAsync(item, profileId, cancellationToken, overrideWatched).ConfigureAwait(false);

        // A track nobody can select is not a track. Deleting a sidecar here already re-read the
        // item; writing one did not, so a file asked for by hand stayed invisible until something
        // else — a scan, or the next scheduled run's refresh of some other item — happened past it.
        if (results.Any(result => result.Outcome == ProcessingOutcome.Written))
        {
            await _refresher.RefreshNowAsync(item, cancellationToken).ConfigureAwait(false);
        }

        return Ok(results);
    }

    /// <summary>
    /// Recursively enqueues and processes all video items under a folder, series, or season.
    /// </summary>
    [HttpPost("/Plugins/DialogueBoost/Folders/{folderId}/Process")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> ProcessFolder(
        [FromRoute] Guid folderId,
        [FromQuery] string? profileId,
        [FromQuery] bool overrideWatched = true,
        CancellationToken cancellationToken = default)
    {
        var folderItem = _library.ById(folderId) as Folder;
        if (folderItem == null)
        {
            return NotFound($"Folder item with ID {folderId} not found.");
        }

        var videoItems = folderItem.GetRecursiveChildren(ProcessableItem.Is)
            .ToList();

        int written = 0;
        int alreadyDone = 0;
        int skipped = 0;
        int failed = 0;

        var config = Plugin.Instance?.Configuration;
        int maxJobs = Math.Clamp(config?.MaxConcurrentJobs ?? 1, 1, 32);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxJobs,
            CancellationToken = cancellationToken
        };

        var refreshable = new ConcurrentBag<BaseItem>();

        await Parallel.ForEachAsync(videoItems, parallelOptions, async (item, ct) =>
        {
            var results = await _itemProcessor.ProcessItemAsync(item, profileId, ct, overrideWatched).ConfigureAwait(false);
            foreach (var result in results)
            {
                switch (result.Outcome)
                {
                    case ProcessingOutcome.Written:
                        Interlocked.Increment(ref written);
                        refreshable.Add(item);
                        break;
                    case ProcessingOutcome.AlreadyDone: Interlocked.Increment(ref alreadyDone); break;
                    case ProcessingOutcome.Failed: Interlocked.Increment(ref failed); break;
                    default: Interlocked.Increment(ref skipped); break;
                }
            }
        }).ConfigureAwait(false);

        // After the loop and not inside it, exactly as the scheduled run does it — one item can
        // appear twice when two profiles wrote for it, and re-reading it twice is work for nothing.
        await _refresher
            .RefreshBatchAsync(refreshable.Distinct().ToList(), cancellationToken)
            .ConfigureAwait(false);

        return Ok(new
        {
            FolderId = folderId,
            FolderName = folderItem.Name,
            TotalVideoItems = videoItems.Count,
            WrittenCount = written,
            AlreadyDoneCount = alreadyDone,
            SkippedCount = skipped,
            FailedCount = failed
        });
    }

    /// <summary>
    /// Gets what the plugin is set to do, and how much of it is still outstanding.
    /// </summary>
    /// <remarks>
    /// The counts cost a query per selected scope and two file checks per expected track, so this
    /// is a page-load endpoint, not something to poll.
    /// </remarks>
    [HttpGet("/Plugins/DialogueBoost/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetStatus(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var selection = await _selectionStore.GetAsync(cancellationToken).ConfigureAwait(false);
        var forecast = await _forecaster.ForecastAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            PluginEnabled = config?.Enabled ?? false,
            DryRun = config?.DryRun ?? false,
            MaxConcurrentJobs = config?.MaxConcurrentJobs ?? 1,

            // Was SelectedLibrariesCount, which counted scopes — rows, not libraries. Five rows
            // inside one library read as "5 libraries" on a server that has three.
            SelectedScopeCount = selection.Scopes.Count,
            CoverNewMedia = selection.CoverNewMedia,

            forecast.CoveredItems,
            forecast.EnabledProfiles,
            forecast.ExpectedTracks,
            forecast.WrittenTracks,
            forecast.SkippedTracks,
            forecast.OutstandingTracks,
            forecast.UnverifiedTracks,
            Skipped = forecast.Skipped.ToDictionary(
                entry => entry.Key.ToString(),
                entry => entry.Value),
            Outstanding = forecast.Outstanding.ToDictionary(
                entry => entry.Key.ToString(),
                entry => entry.Value)
        });
    }

    /// <summary>
    /// Gets paginated processing history.
    /// </summary>
    [HttpGet("/Plugins/DialogueBoost/History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ProcessedItemRecord>>> GetHistory(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? status = null)
    {
        // A page asks for a page. Left unbounded, one request could pull every record the plugin
        // has ever written into memory and down the wire.
        var history = await _stateRepository
            .GetHistoryAsync(Math.Max(skip, 0), Math.Clamp(take, 1, 500), status)
            .ConfigureAwait(false);
        return Ok(history);
    }

    /// <summary>
    /// Deletes a specific sidecar file from disk and triggers a metadata refresh.
    /// </summary>
    [HttpDelete("/Plugins/DialogueBoost/Items/{itemId}/Sidecar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteSidecar(
        [FromRoute] Guid itemId,
        [FromQuery] string? profileId,
        CancellationToken cancellationToken)
    {
        var item = _library.ById(itemId);
        if (item == null)
        {
            return NotFound($"Item with ID {itemId} not found.");
        }

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return BadRequest("Item has no file path.");
        }

        string itemIdStr = itemId.ToString("N");
        var profiles = Plugin.Instance?.Configuration?.GetAllProfiles()
            .Where(p => string.IsNullOrWhiteSpace(profileId) || p.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (profiles != null)
        {
            foreach (var prof in profiles)
            {
                // Both the name the profile writes today and the one its record was written under:
                // a marker renamed since then leaves a file at the old name, and deleting the
                // record is what would otherwise strand it for good.
                var record = await _stateRepository.GetRecordAsync(itemIdStr, prof.Id).ConfigureAwait(false);
                foreach (var path in SidecarNamer.CandidatePaths(item.Path, prof.SidecarNamingMarker)
                             .Append(record?.SidecarPath)
                             .Where(path => !string.IsNullOrWhiteSpace(path))
                             .Distinct(StringComparer.Ordinal))
                {
                    if (!System.IO.File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        System.IO.File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed deleting sidecar file at {Path}", path);
                    }
                }

                await _stateRepository.DeleteRecordAsync(itemIdStr, prof.Id).ConfigureAwait(false);
            }
        }

        await _refresher.RefreshNowAsync(item, cancellationToken).ConfigureAwait(false);
        return Ok(new { Message = "Sidecar file(s) deleted successfully." });
    }

    /// <summary>
    /// Deletes all generated sidecar files and state records for a specific profile.
    /// </summary>
    [HttpDelete("/Plugins/DialogueBoost/Sidecars/DeleteByProfile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> DeleteSidecarsByProfile(
        [FromQuery] string profileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return BadRequest("profileId query parameter is required.");
        }

        var records = await _stateRepository.GetRecordsByProfileAsync(profileId).ConfigureAwait(false);
        int deletedCount = 0;
        var affectedItemIds = new HashSet<Guid>();

        foreach (var record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.SidecarPath) && System.IO.File.Exists(record.SidecarPath))
            {
                try
                {
                    System.IO.File.Delete(record.SidecarPath);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed deleting sidecar file at {Path}", record.SidecarPath);
                }
            }

            await _stateRepository.DeleteRecordAsync(record.ItemId, profileId).ConfigureAwait(false);
            if (Guid.TryParse(record.ItemId, out var itemGuid))
            {
                affectedItemIds.Add(itemGuid);
            }
        }

        // Resolved to items here rather than handed to Jellyfin's refresh queue as ids: that queue
        // can give back a value it never accepted, and an item that has left the library
        // since its record was written simply has nothing left to refresh.
        var affectedItems = affectedItemIds
            .Select(_library.ById)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        await _refresher.RefreshBatchAsync(affectedItems, cancellationToken).ConfigureAwait(false);

        return Ok(new { Message = $"Deleted {deletedCount} sidecar file(s) for profile '{profileId}'.", DeletedCount = deletedCount });
    }

    /// <summary>
    /// Resets state record in SQLite database for item, forcing reprocessing on next run.
    /// </summary>
    [HttpPost("/Plugins/DialogueBoost/Items/{itemId}/Forget")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ForgetRecord(
        [FromRoute] Guid itemId,
        [FromQuery] string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return BadRequest("profileId query parameter is required.");
        }

        string itemIdStr = itemId.ToString("N");
        bool deleted = await _stateRepository.DeleteRecordAsync(itemIdStr, profileId).ConfigureAwait(false);
        if (!deleted)
        {
            return NotFound($"No state record found for item {itemId} and profile {profileId}.");
        }

        return Ok(new { Message = "State record cleared." });
    }

    /// <summary>
    /// Sets or toggles the cleanup exemption status for an item's processed record.
    /// </summary>
    [HttpPost("/Plugins/DialogueBoost/Items/{itemId}/Exempt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetExempt(
        [FromRoute] Guid itemId,
        [FromQuery] string profileId,
        [FromQuery] bool exempt = true)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return BadRequest("profileId query parameter is required.");
        }

        string itemIdStr = itemId.ToString("N");
        bool updated = await _stateRepository.SetExemptAsync(itemIdStr, profileId, exempt).ConfigureAwait(false);
        if (!updated)
        {
            return NotFound($"No state record found for item {itemId} and profile {profileId}.");
        }

        return Ok(new { Message = $"Cleanup exemption updated to {exempt}." });
    }
}
