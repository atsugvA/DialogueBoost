using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DialogueBoost.Integration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// Everything that contains an item, across both of the trees Jellyfin keeps.
/// </summary>
/// <remarks>
/// The parent chain is the physical tree — the library's folder on disk, a series, a season. The row
/// a user calls a library is a <c>CollectionFolder</c>, a node in the *virtual* tree that appears in
/// no item's parent chain, so anything that reasons upwards from an item has to add it explicitly or
/// it will never see the library the item is in.
///
/// Used to answer two questions that are the same question: which rows the config page must mark,
/// and whether an item that just arrived is inside something the user chose.
/// </remarks>
public sealed class ItemContainers
{
    private readonly IMediaLibrary _library;

    public ItemContainers(IMediaLibrary library)
    {
        _library = library;
    }

    /// <summary>
    /// The containers of an item that may or may not still be in the library. An item that is gone
    /// has none.
    /// </summary>
    public IEnumerable<Guid> Of(Guid itemId)
    {
        var item = _library.ById(itemId);
        return item is null ? Array.Empty<Guid>() : Of(item);
    }

    /// <summary>
    /// The containers of an item, nearest first, ending with the libraries it belongs to. Ids may
    /// repeat when both trees lead to the same node; callers that count deduplicate.
    /// </summary>
    public IEnumerable<Guid> Of(BaseItem item)
    {
        var seen = new HashSet<Guid> { item.Id };
        for (var parent = item.GetParent(); parent is not null && seen.Add(parent.Id); parent = parent.GetParent())
        {
            yield return parent.Id;
        }

        // The link between the two trees: CollectionFolder.PhysicalFolderIds points down, this maps
        // back up.
        foreach (var library in _library.LibrariesOf(item))
        {
            yield return library.Id;
        }
    }
}
