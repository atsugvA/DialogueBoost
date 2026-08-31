using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Controller.Entities.Movies;
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
    private readonly FakeMediaLibrary _library;
    private readonly SidecarSweep _sweep;

    public SidecarSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"db_sweep_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "state.db");
        _state = new ProcessingStateRepository(_dbPath, NullLogger<ProcessingStateRepository>.Instance);
        _library = new FakeMediaLibrary(Path.Combine(_root, "metadata"));
        _sweep = new SidecarSweep(
            _library, new SidecarPlacement(_library), _state, NullLogger<SidecarSweep>.Instance);
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

    /// <summary>
    /// What an encode cut short leaves: a temporary file under either naming. Both go; a temporary
    /// file a run is writing right now stays, and so does everything that is not one.
    /// </summary>
    [Fact]
    public void RemoveLeftoverTemps_TakesTheOldOnesUnderEitherName_AndLeavesTheRest()
    {
        string source = Source("Movie F");
        string legacy = Beside("Movie F", "Movie F.Dialogue Boost.default.mka.tmp_0123456789abcdef0123456789abcdef.mka");
        string current = Beside("Movie F", ".dialogueboost-tmp-0123456789abcdef0123456789abcdef.mka");
        string inFlight = Beside("Movie F", ".dialogueboost-tmp-fedcba9876543210fedcba9876543210.mka");
        string track = Beside("Movie F", "Movie F.Dialogue Boost.default.mka");
        string theirs = Beside("Movie F", "Movie F.Commentary.mka");

        var serverStarted = DateTime.UtcNow;
        foreach (var path in new[] { legacy, current, track, theirs })
        {
            File.SetLastWriteTimeUtc(path, serverStarted.AddHours(-1));
        }

        File.SetLastWriteTimeUtc(inFlight, serverStarted.AddSeconds(30));

        int removed = _sweep.RemoveLeftoverTemps(serverStarted, CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(current));
        Assert.True(File.Exists(inFlight));
        Assert.True(File.Exists(track));
        Assert.True(File.Exists(theirs));
        Assert.True(File.Exists(source));
    }

    /// <summary>
    /// A track in the item's metadata folder is as much a track as one beside it — Jellyfin reads
    /// both — so the way out has to find it, whichever folder the setting names today.
    /// </summary>
    [Fact]
    public async Task FindAsync_AlsoLooksInEveryItemsMetadataFolder_AndTakesOnlyWhatIsOurs()
    {
        string source = Source("Movie G");
        string beside = Beside("Movie G", "Movie G.Dialogue Boost.default.mka");
        string inMetadata = InMetadata(source, "Movie G.Broadband Night Mode.mka");
        string jellyfins = InMetadata(source, "Movie G.eng.srt");
        string notOurs = InMetadata(source, "Movie G.Commentary.mka");

        var found = await _sweep.FindAsync(CancellationToken.None);

        Assert.Equal(
            new[] { beside, inMetadata }.OrderBy(p => p, StringComparer.Ordinal),
            found.Files.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Single(found.Items);
        Assert.True(File.Exists(jellyfins));
        Assert.True(File.Exists(notOurs));
    }

    /// <summary>An encode cut short leaves its temporary file wherever it was writing — here too.</summary>
    [Fact]
    public void RemoveLeftoverTemps_AlsoClearsMetadataFolders()
    {
        string source = Source("Movie H");
        string temp = InMetadata(source, ".dialogueboost-tmp-0123456789abcdef0123456789abcdef.mka");
        string track = InMetadata(source, "Movie H.Dialogue Boost.default.mka");
        var serverStarted = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(temp, serverStarted.AddHours(-1));
        File.SetLastWriteTimeUtc(track, serverStarted.AddHours(-1));

        Assert.Equal(1, _sweep.RemoveLeftoverTemps(serverStarted, CancellationToken.None));
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(track));
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

    private string InMetadata(string source, string fileName)
    {
        var item = _library.Items.First(i => string.Equals(i.Path, source, StringComparison.Ordinal));
        string folder = _library.MetadataFolderOf(item);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, fileName);
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
}
