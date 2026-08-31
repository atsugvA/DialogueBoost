using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Jellyfin.Plugin.DialogueBoost.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// What happens to a track that is no longer where its record says it belongs — before anything
/// decides whether to encode.
/// </summary>
public class SidecarBookkeepingTests : IDisposable
{
    private const string ItemId = "item";
    private const string ProfileId = "dialogue-boost";

    private readonly string _root;
    private readonly ProcessingStateRepository _state;
    private readonly SidecarBookkeeping _bookkeeping;

    public SidecarBookkeepingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"db_bookkeeping_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _state = new ProcessingStateRepository(Path.Combine(_root, "state.db"), NullLogger<ProcessingStateRepository>.Instance);
        _bookkeeping = new SidecarBookkeeping(_state, NullLogger<SidecarBookkeeping>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The track goes with its source, and so does the record's idea of where it is. The record
    /// used to keep the old path, and the band then counted the moved track as missing.
    /// </summary>
    [Fact]
    public async Task ASourceThatMoved_TakesItsTrackAlong_AndTheRecordFollows()
    {
        string oldSource = PathTo("Old", "film.mkv");
        string oldTrack = Create("Old", "film.Dialogue Boost.mka");
        string newSource = Create("New", "film (2020).mkv");
        string newTrack = PathTo("New", "film (2020).Dialogue Boost.mka");
        var record = await Recorded(oldSource, oldTrack);

        await _bookkeeping.ReconcileAsync(record, ItemId, newSource, newTrack, dryRun: false);

        Assert.False(File.Exists(oldTrack));
        Assert.True(File.Exists(newTrack));
        var stored = await _state.GetRecordAsync(ItemId, ProfileId);
        Assert.Equal(newSource, stored!.SourcePath);
        Assert.Equal(newTrack, stored.SidecarPath);
        Assert.Equal(newTrack, record.SidecarPath);
    }

    /// <summary>
    /// A track under a name the profile no longer writes is a second row in the audio menu, so it
    /// goes; the encode that follows writes the one that belongs.
    /// </summary>
    [Fact]
    public async Task ATrackUnderANameNoLongerWritten_IsDeleted()
    {
        string source = Create("Film", "film.mkv");
        string oldName = Create("Film", "film.Old Marker.mka");
        var record = await Recorded(source, oldName);

        await _bookkeeping.ReconcileAsync(record, ItemId, source, PathTo("Film", "film.Dialogue Boost.mka"), dryRun: false);

        Assert.False(File.Exists(oldName));
        Assert.True(File.Exists(source));
    }

    /// <summary>
    /// "Decide, but write nothing" — and a move or a delete is writing. Both used to happen here
    /// whatever the setting said, because the bookkeeping runs before the run reads it.
    /// </summary>
    [Fact]
    public async Task ADryRun_MovesNothing_DeletesNothing_AndLeavesTheRecordAlone()
    {
        string oldSource = PathTo("Old", "film.mkv");
        string oldTrack = Create("Old", "film.Dialogue Boost.mka");
        string newSource = Create("New", "film.mkv");
        string renamed = Create("New", "film.Old Marker.mka");

        var moved = await Recorded(oldSource, oldTrack);
        await _bookkeeping.ReconcileAsync(moved, ItemId, newSource, PathTo("New", "film.Dialogue Boost.mka"), dryRun: true);

        Assert.True(File.Exists(oldTrack));
        Assert.False(File.Exists(PathTo("New", "film.Dialogue Boost.mka")));
        var stored = await _state.GetRecordAsync(ItemId, ProfileId);
        Assert.Equal(oldSource, stored!.SourcePath);
        Assert.Equal(oldTrack, stored.SidecarPath);

        var stale = await Recorded(newSource, renamed);
        await _bookkeeping.ReconcileAsync(stale, ItemId, newSource, PathTo("New", "film.Dialogue Boost.mka"), dryRun: true);

        Assert.True(File.Exists(renamed));
    }

    /// <summary>
    /// Changing where tracks live moves the ones already written — in either direction — rather
    /// than encoding them again, and the record follows each one.
    /// </summary>
    [Theory]
    [InlineData("Media", "Metadata")]
    [InlineData("Metadata", "Media")]
    public async Task ATrackInTheOtherFolder_IsMoved_NotEncodedAgain(string from, string to)
    {
        string source = Create("Media", "film.mkv");
        string there = Create(from == "Media" ? "Media" : "Metadata/library/ab/abcd", "film.Dialogue Boost.default.mka");
        string here = PathTo(to == "Media" ? "Media" : "Metadata/library/ab/abcd", "film.Dialogue Boost.default.mka");
        var record = await Recorded(source, there);

        var reconciled = await _bookkeeping.ReconcileAsync(record, ItemId, source, here, dryRun: false);

        Assert.True(reconciled.Moved);
        Assert.Equal(here, reconciled.TrackPath);
        Assert.False(File.Exists(there));
        Assert.Equal("film.Dialogue Boost.default.mka", File.ReadAllText(here));
        Assert.Equal(here, (await _state.GetRecordAsync(ItemId, ProfileId))!.SidecarPath);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(here)!, ".dialogueboost-tmp-*"));
    }

    /// <summary>
    /// The record vouches for the track it names. A file already sitting where it is going — left by
    /// whatever forgot the record — is replaced by it, not the other way round.
    /// </summary>
    [Fact]
    public async Task AMove_ReplacesWhateverIsAlreadyThere()
    {
        string source = Create("Media", "film.mkv");
        string there = Create("Media", "film.Dialogue Boost.default.mka");
        string here = PathTo("Metadata", "film.Dialogue Boost.default.mka");
        File.WriteAllText(here, "a copy nobody vouches for");
        var record = await Recorded(source, there);

        var reconciled = await _bookkeeping.ReconcileAsync(record, ItemId, source, here, dryRun: false);

        Assert.True(reconciled.Moved);
        Assert.Equal("film.Dialogue Boost.default.mka", File.ReadAllText(here));
        Assert.False(File.Exists(there));
    }

    /// <summary>
    /// A track that cannot be moved between the two folders Jellyfin reads is still the track it was
    /// — listed, played, current — so it stays where it is, and the next run tries again.
    /// </summary>
    [Fact]
    public async Task ATrackThatCannotBeMoved_StaysWhereItIs_AndStillCounts()
    {
        string source = Create("Media", "film.mkv");
        string there = Create("Media", "film.Dialogue Boost.default.mka");
        Create("Blocked", "not-a-folder");
        string here = Path.Combine(PathTo("Blocked", "not-a-folder"), "film.Dialogue Boost.default.mka");
        var record = await Recorded(source, there);

        var reconciled = await _bookkeeping.ReconcileAsync(record, ItemId, source, here, dryRun: false);

        Assert.False(reconciled.Moved);
        Assert.Equal(there, reconciled.TrackPath);
        Assert.True(File.Exists(there));
        Assert.Equal(there, (await _state.GetRecordAsync(ItemId, ProfileId))!.SidecarPath);
    }

    [Fact]
    public async Task ADryRun_SaysWhereTheTrackIs_AndLeavesItThere()
    {
        string source = Create("Media", "film.mkv");
        string there = Create("Media", "film.Dialogue Boost.default.mka");
        string here = PathTo("Metadata", "film.Dialogue Boost.default.mka");
        var record = await Recorded(source, there);

        var reconciled = await _bookkeeping.ReconcileAsync(record, ItemId, source, here, dryRun: true);

        Assert.False(reconciled.Moved);
        Assert.Equal(there, reconciled.TrackPath);
        Assert.True(File.Exists(there));
        Assert.False(File.Exists(here));
    }

    [Fact]
    public void RemoveCopies_DeletesWhatIsThere_AndIgnoresWhatIsNot()
    {
        string copy = Create("Media", "film.Dialogue Boost.default.mka");

        _bookkeeping.RemoveCopies(new[] { copy, PathTo("Elsewhere", "film.Dialogue Boost.default.mka") });

        Assert.False(File.Exists(copy));
    }

    /* ── the fixture ────────────────────────────────────────────────────────────────────── */

    private string PathTo(string folder, string fileName)
    {
        Directory.CreateDirectory(Path.Combine(_root, folder));
        return Path.Combine(_root, folder, fileName);
    }

    private string Create(string folder, string fileName)
    {
        string path = PathTo(folder, fileName);
        File.WriteAllText(path, fileName);
        return path;
    }

    private async Task<ProcessedItemRecord> Recorded(string sourcePath, string sidecarPath)
    {
        var record = new ProcessedItemRecord
        {
            ItemId = ItemId,
            ProfileId = ProfileId,
            SourcePath = sourcePath,
            SidecarPath = sidecarPath,
            ParamsHash = "hash",
            ProfileParamsHash = "settings",
            ProcessedLanguages = new List<string> { "eng" },
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };
        await _state.SaveRecordAsync(record);
        return record;
    }
}
