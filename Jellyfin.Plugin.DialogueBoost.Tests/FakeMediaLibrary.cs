using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DialogueBoost.Integration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// One library of movies, each with a metadata folder of its own under
/// <paramref name="metadataRoot"/>.
/// </summary>
/// <remarks>
/// The seam is the only place a test can stand, so the fake is a class rather than a mock. Its
/// metadata folders are laid out the way Jellyfin lays out a library item's — two characters of the
/// id, then the id — so a test that reads a path reads a plausible one; nothing depends on it.
/// </remarks>
internal sealed class FakeMediaLibrary(string metadataRoot) : IMediaLibrary
{
    private readonly BaseItem _library = new Movie { Id = Guid.NewGuid(), Name = "Library" };

    public List<BaseItem> Items { get; } = new();

    public event EventHandler<ItemChangeEventArgs>? ItemAdded { add { } remove { } }

    public BaseItem Add(string path)
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = Path.GetFileName(path), Path = path };
        Items.Add(item);
        return item;
    }

    public string IdOf(string path) =>
        Items.First(item => string.Equals(item.Path, path, StringComparison.Ordinal)).Id.ToString("N");

    public BaseItem? ById(Guid id) => Items.FirstOrDefault(item => item.Id == id);

    public IReadOnlyList<BaseItem> Libraries() => new[] { _library };

    public IReadOnlyList<BaseItem> LibrariesOf(BaseItem item) => new[] { _library };

    public IReadOnlyList<BaseItem> RootFolders() => new[] { _library };

    public IReadOnlyList<BaseItem> Under(Guid parentId, BaseItemKind[] kinds, bool recursive) => Items;

    public IReadOnlyList<BaseItem> Within(BaseItem parent, BaseItemKind[] kinds) => Items;

    public string MetadataFolderOf(BaseItem item)
    {
        string id = item.Id.ToString("N");
        return Path.Combine(metadataRoot, "library", id[..2], id);
    }
}
