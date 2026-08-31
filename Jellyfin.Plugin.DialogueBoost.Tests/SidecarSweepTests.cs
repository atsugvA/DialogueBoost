using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// The most destructive path in the plugin, and therefore the one whose boundary matters most:
/// everything it wrote goes, and everything it did not stays.
/// </summary>
public class SidecarSweepTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly ProcessingStateRepository _state;
    private readonly FakeLibrary _library = new();
    private readonly SidecarSweep _sweep;

    public SidecarSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"db_sweep_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "state.db");
        _state = new ProcessingStateRepository(_dbPath, NullLogger<ProcessingStateRepository>.Instance);
        _sweep = new SidecarSweep(_library, _state, NullLogger<SidecarSweep>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Both names a profile can write, every profile, and nothing else in the folder.
    /// </summary>
    [Fact]
    public async Task FindAsync_TakesTheNamesThePluginWritesAndLeavesEverythingElse()
    {
        string source = Source("Movie A");
        string claimed = Beside("Movie A", "Movie A.Dialogue Boost.default.mka");
        string plain = Beside("Movie A", "Movie A.Broadband Night Mode.mka");
        string theirs = Beside("Movie A", "Movie A.Commentary.mka");
        string alsoTheirs = Beside("Movie A", "Movie A.eng.mka");

        var found = await _sweep.FindAsync(CancellationToken.None);

        Assert.Equal(
            new[] { claimed, plain }.OrderBy(p => p, StringComparer.Ordinal),
            found.Files.OrderBy(p => p, StringComparer.Ordinal));
        Assert.True(File.Exists(theirs));
        Assert.True(File.Exists(alsoTheirs));
        Assert.True(File.Exists(source));
    }

    /// <summary>
    /// A marker nobody uses any more is only findable through the record that named it.
    /// </summary>
    [Fact]
    public async Task FindAsync_AlsoTakesWhatARecordNames()
    {
        Source("Movie B");
        string renamed = Beside("Movie B", "Movie B.Some Old Marker.mka");
        await Record("Movie B", renamed);

        var found = await _sweep.FindAsync(CancellationToken.None);

        Assert.Equal(new[] { renamed }, found.Files);
    }

    /// <summary>
    /// 26 of this machine's 83 records pointed at files that no longer existed. That is not an
    /// error and must not become one.
    /// </summary>
    [Fact]
    public async Task FindAsync_IgnoresARecordWhoseFileIsGone()
    {
        Source("Movie C");
        await Record("Movie C", Path.Combine(_root, "gone", "Movie C.Dialogue Boost.mka"));

        var found = await _sweep.FindAsync(CancellationToken.None);

        Assert.Empty(found.Files);
    }

    [Fact]
    public async Task RemoveAsync_DeletesWhatWasFound_ClearsEveryRecord_AndKeepsTheSources()
    {
        string source = Source("Movie D");
        string ours = Beside("Movie D", "Movie D.Dialogue Boost.default.mka");
        string theirs = Beside("Movie D", "Movie D.Commentary.mka");
        await Record("Movie D", ours);
        await Record("Movie D", Path.Combine(_root, "gone", "stale.mka"), profileId: "speech");

        var found = await _sweep.FindAsync(CancellationToken.None);
        var (deleted, failed, records) = await _sweep.RemoveAsync(found, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(0, failed);
        Assert.Equal(2, records);                       // the stale one is cleared too
        Assert.False(File.Exists(ours));
        Assert.True(File.Exists(theirs));
        Assert.True(File.Exists(source));
        Assert.Empty(await _state.GetAllRecordsAsync());
    }

    /// <summary>A folder that has since been deleted is not a reason to fail the whole sweep.</summary>
    [Fact]
    public async Task FindAsync_SurvivesAnItemWhoseFolderIsGone()
    {
        string source = Source("Movie E");
        string ours = Beside("Movie E", "Movie E.Dialogue Boost.default.mka");
        _library.Add(Path.Combine(_root, "Vanished", "Vanished.mkv"));

        var found = await _sweep.FindAsync(CancellationToken.None);

        Assert.Equal(new[] { ours }, found.Files);
        Assert.True(File.Exists(source));
    }

    /// <summary>An item with no path at all — a virtual entry — is skipped rather than thrown on.</summary>
    [Fact]
    public async Task FindAsync_SkipsAnItemWithNoPath()
    {
        _library.Items.Add(new Movie { Id = Guid.NewGuid(), Name = "No path" });

        var found = await _sweep.FindAsync(CancellationToken.None);

        Assert.Empty(found.Files);
    }

    /* ── the fixture ────────────────────────────────────────────────────────────────────── */

    private string Source(string name)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".mkv");
        File.WriteAllText(path, "source");
        _library.Add(path);
        return path;
    }

    private string Beside(string folder, string fileName)
    {
        string path = Path.Combine(_root, folder, fileName);
        File.WriteAllText(path, "track");
        return path;
    }

    private Task Record(string folder, string sidecarPath, string profileId = "dialogue-boost") =>
        _state.SaveRecordAsync(new ProcessedItemRecord
        {
            ItemId = _library.IdOf(Path.Combine(_root, folder, folder + ".mkv")),
            ProfileId = profileId,
            SourcePath = Path.Combine(_root, folder, folder + ".mkv"),
            SidecarPath = sidecarPath,
            Status = "Success",
            ParamsHash = "hash",
            ProfileParamsHash = "hash",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        });

    /// <summary>
    /// The seam is the only place a test can stand, so the fake is a class rather than a mock.
    /// </summary>
    private sealed class FakeLibrary : IMediaLibrary
    {
        private readonly BaseItem _library = new Movie { Id = Guid.NewGuid(), Name = "Library" };

        public List<BaseItem> Items { get; } = new();

        public event EventHandler<ItemChangeEventArgs>? ItemAdded { add { } remove { } }

        public void Add(string path) =>
            Items.Add(new Movie { Id = Guid.NewGuid(), Name = Path.GetFileName(path), Path = path });

        public string IdOf(string path) =>
            Items.First(item => string.Equals(item.Path, path, StringComparison.Ordinal)).Id.ToString("N");

        public BaseItem? ById(Guid id) => Items.FirstOrDefault(item => item.Id == id);

        public IReadOnlyList<BaseItem> Libraries() => new[] { _library };

        public IReadOnlyList<BaseItem> LibrariesOf(BaseItem item) => new[] { _library };

        public IReadOnlyList<BaseItem> RootFolders() => new[] { _library };

        public IReadOnlyList<BaseItem> Under(Guid parentId, BaseItemKind[] kinds, bool recursive) => Items;

        public IReadOnlyList<BaseItem> Within(BaseItem parent, BaseItemKind[] kinds) => Items;
    }
}
