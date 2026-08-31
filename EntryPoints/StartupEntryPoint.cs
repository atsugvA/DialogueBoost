using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.EntryPoints;

/// <summary>
/// The work the plugin does when the server comes up: clear what an interrupted run left behind,
/// and run a daily task whose slot passed while the server was off.
/// </summary>
/// <remarks>
/// It deliberately does not check the encoder. It used to, and it checked the wrong one: a hosted
/// service starts about two seconds before Jellyfin publishes `IMediaEncoder.EncoderPath`, so the
/// check always fell through to whatever `ffmpeg` on PATH resolved to — reporting the system build
/// 9.0.1 while every encode used jellyfin-ffmpeg 7.1.4, and raising `CRITICAL: ffmpeg binary was
/// not found` on hosts that have no system ffmpeg at all and never needed one. Jellyfin logs
/// `FFmpeg: &lt;path&gt;` itself a second later, and `ProcessRunner` names the binary and version it
/// is actually driving on its first run.
/// </remarks>
public class StartupEntryPoint : IHostedService
{
    private readonly SidecarSweep _sweep;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<StartupEntryPoint> _logger;

    public StartupEntryPoint(
        SidecarSweep sweep,
        ITaskManager taskManager,
        ILogger<StartupEntryPoint> logger)
    {
        _sweep = sweep;
        _taskManager = taskManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RemoveLeftoverTemps(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        RunIfDailyRunWasMissed(PluginTasks.CleanupKey, TimeSpan.FromSeconds(10));
        RunIfDailyRunWasMissed(PluginTasks.NormalizeKey, TimeSpan.FromSeconds(15));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Deletes what an encode cut short by a crash or a hard stop left behind.
    /// </summary>
    /// <remarks>
    /// It used to walk <c>RootFolder.Children</c>, whose paths are Jellyfin's own
    /// <c>root/default/&lt;library&gt;</c> folders — the <c>.mblink</c> files that point at the media,
    /// not the media — so it never reached a folder a sidecar is written into. It asks the library
    /// for its items now, as the sweep does, and in the background: a walk over the library is not
    /// something Jellyfin's startup should wait for. Only files last written before this server
    /// started are touched, so a run that begins meanwhile keeps its own.
    /// </remarks>
    private void RemoveLeftoverTemps(DateTime serverStartedUtc, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);

                int removed = _sweep.RemoveLeftoverTemps(serverStartedUtc, CancellationToken.None);
                if (removed > 0)
                {
                    _logger.LogInformation(
                        "DialogueBoost: removed {Count} temporary file(s) left by encodes that never finished.", removed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DialogueBoost: could not clear the temporary files of unfinished encodes.");
            }
        });
    }

    /// <summary>
    /// Runs a daily task whose run was missed while the server was off.
    /// </summary>
    /// <remarks>
    /// The delay is not cosmetic: the plugin's hosted services start before Jellyfin has registered
    /// the scheduled tasks, so both the schedule and the last run are unreadable until it has. What
    /// the task should be doing is therefore decided after the wait, not before it.
    /// </remarks>
    private void RunIfDailyRunWasMissed(string taskKey, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);

                if (Plugin.Instance?.Configuration.Enabled != true)
                {
                    return;
                }

                var worker = PluginTasks.Find(_taskManager, taskKey);
                if (worker is null)
                {
                    _logger.LogWarning("DialogueBoost: scheduled task '{Key:l}' is not registered.", taskKey);
                    return;
                }

                var lastRun = worker.LastExecutionResult?.StartTimeUtc;
                if (!DailyRunCatchUp.WasMissed(worker.Triggers, lastRun, DateTime.UtcNow))
                {
                    return;
                }

                _logger.LogInformation(
                    "DialogueBoost: '{Task:l}' runs daily but last ran {LastRun:l} — catching up now.",
                    worker.Name,
                    lastRun?.ToString("u", CultureInfo.InvariantCulture) ?? "never");

                await _taskManager.Execute(worker, new TaskOptions()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DialogueBoost: failed to catch up the missed run of '{Key:l}'.", taskKey);
            }
        });
    }
}
