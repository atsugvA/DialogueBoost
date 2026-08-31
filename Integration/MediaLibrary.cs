using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.DialogueBoost.Integration;

/// <summary>
/// The questions this plugin asks Jellyfin's library. Every <see cref="ILibraryManager"/> call the
/// plugin makes is behind this, so a change to that surface lands in one file.
/// </summary>
/// <remarks>
/// <see cref="BaseItem"/> itself stays: it is Jellyfin's domain type and the plugin reads little
/// from it — id, name, path, parent, creation date. What churns, and what is therefore wrapped, is
/// the *query* surface: <c>InternalItemsQuery</c>, the virtual-folder listing, and the two-tree
/// distinction that <see cref="LibrariesOf"/> exists to bridge.
/// </remarks>
public interface IMediaLibrary
{
    /// <summary>One item, or <c>null</c> if it is no longer in the library.</summary>
    BaseItem? ById(Guid id);

    /// <summary>The libraries, as items — the tree's top level.</summary>
    IReadOnlyList<BaseItem> Libraries();

    /// <summary>The libraries an item belongs to. Not the same as walking its parents.</summary>
    IReadOnlyList<BaseItem> LibrariesOf(BaseItem item);

    /// <summary>The media folders below Jellyfin's root, for walking the files on disk.</summary>
    IReadOnlyList<BaseItem> RootFolders();

    /// <summary>
    /// Items of the given kinds under a parent id, by query rather than by the in-memory child
    /// tree — which returns nothing for a season Jellyfin synthesised from episode filenames.
    /// </summary>
    IReadOnlyList<BaseItem> Under(Guid parentId, BaseItemKind[] kinds, bool recursive);

    /// <summary>
    /// Everything of the given kinds anywhere under a parent, handing Jellyfin the parent itself.
    /// A collection folder is then queried by the physical folders behind it and an ordinary folder
    /// by ancestry, and which is which stays Jellyfin's business.
    /// </summary>
    IReadOnlyList<BaseItem> Within(BaseItem parent, BaseItemKind[] kinds);

    /// <summary>
    /// Jellyfin's own folder for one item: where it keeps the item's images and downloaded
    /// subtitles, which it reads external audio from exactly as it does beside the media, and which
    /// it deletes when the item leaves the library. It need not exist yet — Jellyfin creates
    /// it the first time it stores something there.
    /// </summary>
    string MetadataFolderOf(BaseItem item);

    /// <summary>Raised when Jellyfin adds an item, whether by scan or by file watch.</summary>
    event EventHandler<ItemChangeEventArgs> ItemAdded;
}

/// <inheritdoc />
public sealed class JellyfinMediaLibrary : IMediaLibrary
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinMediaLibrary(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public event EventHandler<ItemChangeEventArgs> ItemAdded
    {
        add => _libraryManager.ItemAdded += value;
        remove => _libraryManager.ItemAdded -= value;
    }

    public BaseItem? ById(Guid id) => _libraryManager.GetItemById(id);

    public IReadOnlyList<BaseItem> Libraries() =>
        _libraryManager.GetVirtualFolders()
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? _libraryManager.GetItemById(id) : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

    public IReadOnlyList<BaseItem> LibrariesOf(BaseItem item) => _libraryManager.GetCollectionFolders(item);

    public IReadOnlyList<BaseItem> RootFolders() => _libraryManager.RootFolder.Children.ToList();

    public IReadOnlyList<BaseItem> Under(Guid parentId, BaseItemKind[] kinds, bool recursive) =>
        _libraryManager.GetItemList(Query(kinds, recursive, parentId));

    public IReadOnlyList<BaseItem> Within(BaseItem parent, BaseItemKind[] kinds) =>
        _libraryManager.GetItemList(Query(kinds, recursive: true), new List<BaseItem> { parent });

    /// <summary>
    /// Asked of the item rather than built from the server's metadata path, because the layout
    /// below that path — <c>library/&lt;first two of the id&gt;/&lt;id&gt;</c> for a library item,
    /// something else for a channel's — is Jellyfin's to change, and its own readers ask the item.
    /// </summary>
    public string MetadataFolderOf(BaseItem item) => item.GetInternalMetadataPath();

    /// <summary>
    /// One query shape for both forms. <c>IsVirtualItem = false</c> is not a caller's choice:
    /// an episode a metadata provider knows about but nobody has is not a row and not work.
    /// </summary>
    private static InternalItemsQuery Query(BaseItemKind[] kinds, bool recursive, Guid parentId = default) =>
        new()
        {
            ParentId = parentId,
            Recursive = recursive,
            IncludeItemTypes = kinds,
            IsVirtualItem = false
        };
}
