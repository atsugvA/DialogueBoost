using System.Threading;

namespace Jellyfin.Plugin.DialogueBoost.ScheduledTasks;

/// <summary>
/// A one-off instruction for the next watched-audio cleanup run.
/// </summary>
/// <remarks>
/// "Purge exempt now" used to be expressed by writing <c>IncludeExemptInCleanup = true</c> into the
/// plugin configuration and starting the task — a permanent settings change nobody asked for, so
/// one press of that button quietly changed every later scheduled run as well, and rewrote the whole
/// configuration document while doing it. The instruction belongs to the run, not to the
/// settings, so it lives here for exactly one run.
/// </remarks>
public sealed class CleanupRunOverride
{
    private int _includeExemptOnce;

    /// <summary>
    /// Asks the next run to delete sidecars that are marked exempt from cleanup.
    /// </summary>
    public void IncludeExemptOnNextRun() => Interlocked.Exchange(ref _includeExemptOnce, 1);

    /// <summary>
    /// Reads the instruction and clears it, so it can only ever apply once.
    /// </summary>
    public bool TakeIncludeExempt() => Interlocked.Exchange(ref _includeExemptOnce, 0) == 1;
}
