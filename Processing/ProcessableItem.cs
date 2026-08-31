using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// The one definition of "a video this plugin can put a sidecar beside".
/// </summary>
public static class ProcessableItem
{
    /// <summary>
    /// The same set as <see cref="Is"/>, in the form a library query takes. Kept beside it so a
    /// query and the predicate that filters its result cannot come to disagree.
    /// </summary>
    public static readonly BaseItemKind[] Kinds =
    {
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.Video
    };

    /// <summary>
    /// Gets a value indicating whether the item is one the plugin processes.
    /// </summary>
    /// <remarks>
    /// Kept verbatim from the five identical copies that used to sit in the two scheduled tasks and
    /// the API controller. It lives in one place so that what a run processes and what the watcher
    /// notices cannot drift apart.
    /// </remarks>
    public static bool Is(BaseItem? item) =>
        item is Movie || item is Episode || (item is Video video && !video.IsShortcut);
}
