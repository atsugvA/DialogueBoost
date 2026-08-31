using System.Linq;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DialogueBoost.ScheduledTasks;

/// <summary>
/// The plugin's scheduled tasks, by the key Jellyfin knows them under.
/// </summary>
/// <remarks>
/// The keys used to be string literals repeated across the tasks, the API controller and the
/// startup entry point — six copies of two strings, where a typo in any of them silently means
/// "task not found" rather than a compile error.
/// </remarks>
public static class PluginTasks
{
    /// <summary>Key of the library normalization task.</summary>
    public const string NormalizeKey = "DialogueBoostNormalizeTask";

    /// <summary>Key of the watched-audio cleanup task.</summary>
    public const string CleanupKey = "DialogueBoostCleanUpWatchedTask";

    /// <summary>
    /// Finds a task by key, or <c>null</c> if Jellyfin has not registered it (yet).
    /// </summary>
    public static IScheduledTaskWorker? Find(ITaskManager taskManager, string key) =>
        taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask?.Key == key);
}
