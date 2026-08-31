using System;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.ScheduledTasks;

/// <summary>
/// Reads and writes when a plugin task runs daily — on Jellyfin's schedule, which is the only place
/// that answer lives.
/// </summary>
/// <remarks>
/// The plugin used to keep a bool per task and build a trigger from it in
/// <c>GetDefaultTriggers()</c>. Jellyfin consults defaults only while a task has no stored schedule,
/// so the bool took effect a restart late and stopped meaning anything the moment anyone set the
/// schedule from the dashboard — while the config page went on rendering it as a live control
///.
///
/// Here the schedule is read from the task worker and written back to it: effective immediately, and
/// identical whether it was set from this plugin's page or from Jellyfin's own.
/// </remarks>
public sealed class DailySchedule
{
    private readonly ITaskManager _taskManager;
    private readonly ILogger<DailySchedule> _logger;

    public DailySchedule(ITaskManager taskManager, ILogger<DailySchedule> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// Reads a time of day as the config page writes it: <c>HH:mm</c>, 00:00 to 23:59. Anything
    /// else — a blank, "2 AM", 25:00 — is refused rather than rounded into something plausible.
    /// </summary>
    public static bool TryParseTimeOfDay(string? text, out TimeSpan timeOfDay)
    {
        timeOfDay = default;

        if (!TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var parsed)
            || parsed < TimeSpan.Zero
            || parsed >= TimeSpan.FromDays(1))
        {
            return false;
        }

        timeOfDay = parsed;
        return true;
    }

    /// <summary>
    /// Writes a time of day back in the same form.
    /// </summary>
    public static string Format(TimeSpan timeOfDay) =>
        timeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The time of day the task runs, or <c>null</c> if it has no daily trigger — which is also
    /// what an unregistered task answers.
    /// </summary>
    public TimeSpan? Read(string taskKey)
    {
        var worker = PluginTasks.Find(_taskManager, taskKey);
        var daily = worker?.Triggers?.FirstOrDefault(t => t.Type == TaskTriggerInfoType.DailyTrigger);

        return daily?.TimeOfDayTicks is long ticks ? TimeSpan.FromTicks(ticks) : null;
    }

    /// <summary>
    /// Sets the daily run, or clears it with <c>null</c>. Any other trigger on the task — an
    /// interval, a startup trigger, one somebody added from the dashboard — is left alone.
    /// </summary>
    /// <returns><c>false</c> if Jellyfin has no such task registered.</returns>
    public bool Write(string taskKey, TimeSpan? timeOfDay)
    {
        var worker = PluginTasks.Find(_taskManager, taskKey);
        if (worker is null)
        {
            _logger.LogWarning("DialogueBoost: cannot schedule '{Key:l}' — Jellyfin has no such task.", taskKey);
            return false;
        }

        var triggers = worker.Triggers
            .Where(t => t.Type != TaskTriggerInfoType.DailyTrigger)
            .ToList();

        if (timeOfDay is TimeSpan at)
        {
            triggers.Add(new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = at.Ticks
            });
        }

        // Assigning stores the schedule — which is also what stops the plugin's defaults from being
        // consulted again, so "off" stays off across restarts.
        worker.Triggers = triggers;
        worker.ReloadTriggerEvents();

        _logger.LogInformation(
            "DialogueBoost: '{Task:l}' now runs {When:l}.",
            worker.Name,
            timeOfDay is TimeSpan t ? $"daily at {t:hh\\:mm}" : "only when started by hand");

        return true;
    }
}
