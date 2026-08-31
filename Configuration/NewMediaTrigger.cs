namespace Jellyfin.Plugin.DialogueBoost.Configuration;

/// <summary>
/// What the plugin does when media appears in a selected scope while the server is running.
/// </summary>
/// <remarks>
/// Both modes are first-class and permanent: one is not a staging post for the other. Either way the
/// arrival itself is never stored — what a run processes is resolved from the selected scopes at run
/// time, so a missed event costs a delay and nothing else.
/// </remarks>
public enum NewMediaTrigger
{
    /// <summary>
    /// Leave it to the next scheduled run. Nothing starts by itself; the arrival is noted in the log
    /// and picked up by the nightly run.
    /// </summary>
    OnScheduledRun = 0,

    /// <summary>
    /// Start the normalization task once arrivals have been quiet for the settle window — a batch of
    /// downloads is processed the same evening rather than the next night.
    /// </summary>
    WhenSettled = 1
}
