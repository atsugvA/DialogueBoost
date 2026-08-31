using System;
using System.Globalization;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// One step of a <see cref="ScopePath"/>: either one specific library entity, or a group of
/// same-named siblings.
/// </summary>
/// <remarks>
/// The group form exists because a library laid out as one torrent release folder per episode makes
/// Jellyfin build a separate <c>Series</c> entity per folder — nine of them for one show. The
/// user means the show, so a row has to be able to name the show rather than one of the nine, and a
/// release folder that lands tomorrow is a *sibling* of the nine, not a child: containment cannot
/// cover it, only a key can.
///
/// The key is the item's kind and its name, and deliberately nothing else. Both alternatives were
/// measured on the live library and rejected:
///
/// * **Provider ids** group identically to the name on all 50 series and 18 movies here — same
///   groups, no disagreement either way — while being absent on 15 items, so they buy nothing and
///   cost a fallback.
/// * **The production year** would be a sharper key (it separates remakes) but it is metadata: the
///   nine <c>Tin Sparrow</c> folders carry no year in their names and get 2024 from TVDB. A release folder
///   that has just landed therefore has no year yet, and keying on one would split it out of the
///   group it belongs to for exactly as long as the metadata fetch takes.
///
/// The name is metadata too, so a brand-new folder joins its group only once Jellyfin has named it —
/// an unavoidable latency, documented rather than papered over, and the reason the library scope
/// stays the recommendation for hands-off coverage.
/// </remarks>
public static class ScopeSegment
{
    private const string ItemPrefix = "i:";
    private const string GroupPrefix = "k:";

    /// <summary>
    /// Names exactly one entity — a library row, or one release folder picked out of a group.
    /// </summary>
    public static string ForItem(Guid id) => ItemPrefix + Key(id);

    /// <summary>
    /// The bare form of an id — 32 hex characters, no dashes. What the processed-items table keys a
    /// record by, and what the config page keys a row by.
    /// </summary>
    public static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Names every sibling that presents as the same thing, which is usually one item and sometimes
    /// nine. An item with no name cannot be grouped, and stands for itself.
    /// </summary>
    public static string ForGroup(BaseItem item) =>
        string.IsNullOrWhiteSpace(item.Name)
            ? ForItem(item.Id)
            : GroupPrefix + item.GetBaseItemKind().ToString() + "|" + Normalize(item.Name);

    /// <summary>
    /// Gets the item id an item segment names, or <see cref="Guid.Empty"/> for a group segment.
    /// </summary>
    /// <remarks>
    /// A bare id with no prefix is accepted too: that is how a selection stored before the plugin
    /// had a tree was written, and reading it as "exactly this item" is what keeps such a selection
    /// covering precisely what it covered before, whether or not the upgrade to real tree paths has
    /// run yet.
    /// </remarks>
    public static Guid ItemId(string segment)
    {
        if (segment.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            return Guid.Empty;
        }

        var text = segment.StartsWith(ItemPrefix, StringComparison.Ordinal)
            ? segment.AsSpan(ItemPrefix.Length)
            : segment.AsSpan();

        return Guid.TryParse(text, out var id) ? id : Guid.Empty;
    }

    /// <summary>
    /// Whether a candidate belongs under this segment. One comparison for both forms, so what the
    /// tree draws and what a run resolves cannot come to disagree.
    /// </summary>
    public static bool Matches(string segment, BaseItem candidate)
    {
        var wanted = ItemId(segment);
        return wanted == Guid.Empty
            ? string.Equals(segment, ForGroup(candidate), StringComparison.Ordinal)
            : wanted == candidate.Id;
    }

    /// <summary>
    /// The comparable form of a name: trimmed and lower-cased invariantly, so a row keeps its
    /// identity across a rename that only changed capitalisation or padding.
    /// </summary>
    public static string Normalize(string name) => name.Trim().ToLowerInvariant();
}
