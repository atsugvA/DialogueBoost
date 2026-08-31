using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// Where a row sits among its siblings: the number an episode carries, and the order that number
/// puts a level in.
/// </summary>
/// <remarks>
/// A name does not answer "which one is this". Jellyfin names an episode by its title when a
/// provider supplied one — <c>The Long Way Home</c>, <c>Second Thoughts</c> — and by its filename when
/// nothing did, which is what the nine <c>Tin Sparrow</c> release folders show. Neither form says whether
/// the row is the first episode of the season or the seventh, and ordering a level by name puts the
/// episodes in an order nobody watches them in.
///
/// The number is not inferred here: Jellyfin has already parsed it out of the filename, and all 284
/// episodes in the library this was measured on carry both <c>IndexNumber</c> and <c>ParentIndexNumber</c>.
/// A row that has no number is left unnumbered rather than counted into one — it sorts after the
/// numbered rows, by name, which is where an extras folder beside <c>Season 1</c> belongs.
/// </remarks>
public static class RowNumber
{
    /// <summary>
    /// What an unnumbered row sorts as: after everything that has a number.
    /// </summary>
    private const int Unnumbered = int.MaxValue;

    /// <summary>
    /// The number a row shows, or <c>null</c> when nothing numbers it.
    /// </summary>
    /// <remarks>
    /// Episodes only. A season row already reads <c>Season 2</c>, a movie is not one of a sequence
    /// and a library is unique — numbering those would only repeat the name back at the user.
    /// </remarks>
    public static string? Of(BaseItem item) =>
        item is Episode episode ? Format(episode.IndexNumber, episode.IndexNumberEnd) : null;

    /// <summary>
    /// Where a row sorts among its siblings. This is what puts a season's episodes in the order they
    /// happen, and <c>Season 10</c> after <c>Season 9</c> instead of next to <c>Season 1</c>.
    /// </summary>
    public static int Position(BaseItem item) => item.IndexNumber ?? Unnumbered;

    /// <summary>
    /// How a number reads: <c>E3</c>, or <c>E3–4</c> for one file holding two episodes.
    /// </summary>
    /// <param name="number">The episode's number, if it has one.</param>
    /// <param name="last">The last episode in the file, when the file holds more than one.</param>
    public static string? Format(int? number, int? last)
    {
        if (number is not int first)
        {
            return null;
        }

        return last is int end && end > first
            ? FormattableString.Invariant($"E{first}–{end}")
            : FormattableString.Invariant($"E{first}");
    }
}
