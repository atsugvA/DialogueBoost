using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DialogueBoost.ScheduledTasks;

/// <summary>
/// Decides whether a daily task's run was missed while the server was off.
/// </summary>
/// <remarks>
/// Both inputs come from Jellyfin rather than from the plugin's own copy of them. The schedule is
/// read from the task's live triggers, because the plugin's toggle stops meaning anything the
/// moment anyone sets the schedule from the dashboard; the last run is read from the task's
/// own execution result, which Jellyfin persists across restarts — the plugin's duplicate of it was
/// not even written after a dry run, so a dry-run user got a "missed" catch-up on every start.
/// </remarks>
public static class DailyRunCatchUp
{
    /// <summary>How long after the last run a daily task counts as overdue.</summary>
    public static readonly TimeSpan Overdue = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets a value indicating whether a task scheduled to run daily has not run within a day.
    /// </summary>
    /// <param name="triggers">The task's triggers, as Jellyfin currently has them.</param>
    /// <param name="lastRunUtc">When the task last started, or <c>null</c> if it never has.</param>
    /// <param name="nowUtc">The current time.</param>
    public static bool WasMissed(
        IReadOnlyList<TaskTriggerInfo>? triggers,
        DateTime? lastRunUtc,
        DateTime nowUtc)
    {
        if (triggers is null || !triggers.Any(t => t.Type == TaskTriggerInfoType.DailyTrigger))
        {
            return false;
        }

        return lastRunUtc is null || nowUtc - lastRunUtc.Value >= Overdue;
    }
}
