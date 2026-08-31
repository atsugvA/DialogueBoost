using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Api;

/// <summary>
/// The way out: everything this plugin wrote, gone, in one call.
/// </summary>
/// <remarks>
/// The way in is deliberately granular — a profile, a watched item, one track — and every one of
/// those buttons is on the same page. Together they do not add up to "everything", and asking a
/// user who has decided to remove the plugin to work out which combination does is asking the
/// wrong person. Worse, none of them stops the plugin writing the tracks back: with the daily run
/// still scheduled, a library emptied at two in the afternoon is full again by three in the
/// morning. So this endpoint stands the plugin down *before* it deletes, and the order matters.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class RemovalController : ControllerBase
{
    private readonly SidecarSweep _sweep;
    private readonly IMetadataRefresher _refresher;
    private readonly DailySchedule _schedule;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<RemovalController> _logger;

    public RemovalController(
        SidecarSweep sweep,
        IMetadataRefresher refresher,
        DailySchedule schedule,
        ITaskManager taskManager,
        ILogger<RemovalController> logger)
    {
        _sweep = sweep;
        _refresher = refresher;
        _schedule = schedule;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// How much a full removal would delete, without deleting any of it.
    /// </summary>
    /// <remarks>
    /// So the page's question can name the number. "2,481 tracks in 312 folders" is a warning; "are
    /// you sure?" is a formality.
    /// </remarks>
    [HttpGet("/Plugins/DialogueBoost/Sidecars/All")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> CountEverything(CancellationToken cancellationToken)
    {
        var found = await _sweep.FindAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            Tracks = found.Files.Count,
            Folders = found.Folders,
            Items = found.Items.Count
        });
    }

    /// <summary>
    /// Deletes every track this plugin has written, clears its records, unschedules both daily runs
    /// and switches the plugin off.
    /// </summary>
    /// <remarks>
    /// Source files are not touched — nothing here opens one, and the only deletions are of paths
    /// <see cref="SidecarSweep"/> matched against the names this plugin writes.
    /// </remarks>
    [HttpDelete("/Plugins/DialogueBoost/Sidecars/All")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> RemoveEverything(CancellationToken cancellationToken)
    {
        // Stand down first. A run in flight is writing the very files this is about to delete, and
        // a trigger left in place would write them again tonight.
        _taskManager.CancelIfRunning<NormalizeLibraryTask>();
        _taskManager.CancelIfRunning<CleanUpWatchedTask>();
        _schedule.Write(PluginTasks.NormalizeKey, null);
        _schedule.Write(PluginTasks.CleanupKey, null);

        bool disabled = Disable();

        _logger.LogInformation(
            "DialogueBoost: removing everything this plugin has written. Daily runs unscheduled, plugin {State:l}.",
            disabled ? "disabled" : "could not be disabled");

        var found = await _sweep.FindAsync(cancellationToken).ConfigureAwait(false);
        var (deleted, failed, records) = await _sweep.RemoveAsync(found, cancellationToken).ConfigureAwait(false);

        // The items are re-read so Jellyfin stops offering a track whose file is gone. Resolved to
        // items rather than handed to Jellyfin's refresh queue as ids, which can give back a value
        // it never accepted.
        await _refresher.RefreshBatchAsync(found.Items, cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            TracksDeleted = deleted,
            TracksFailed = failed,
            RecordsCleared = records,
            ItemsRefreshed = found.Items.Count,
            DailyRunsCleared = true,
            PluginDisabled = disabled
        });
    }

    /// <summary>
    /// Switches the plugin off and writes that to disk, so a restart does not undo it.
    /// </summary>
    private bool Disable()
    {
        var plugin = Plugin.Instance;
        if (plugin?.Configuration is null)
        {
            return false;
        }

        try
        {
            var configuration = plugin.Configuration;
            configuration.Enabled = false;
            plugin.UpdateConfiguration(configuration);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DialogueBoost: could not switch the plugin off.");
            return false;
        }
    }
}
