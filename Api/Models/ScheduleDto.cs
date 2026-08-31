namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// When the plugin's two tasks run, as Jellyfin has them scheduled right now.
/// </summary>
public class ScheduleDto
{
    /// <summary>
    /// Gets or sets the daily run of the library normalization task.
    /// </summary>
    public DailyRunDto Normalization { get; set; } = new();

    /// <summary>
    /// Gets or sets the daily run of the watched-audio cleanup task.
    /// </summary>
    public DailyRunDto Cleanup { get; set; } = new();
}

/// <summary>
/// One task's daily run.
/// </summary>
public class DailyRunDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the task has a daily trigger at all.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the time of day it runs, as <c>HH:mm</c> in the server's local time. Null when
    /// the task has no daily trigger.
    /// </summary>
    public string? TimeOfDay { get; set; }
}
