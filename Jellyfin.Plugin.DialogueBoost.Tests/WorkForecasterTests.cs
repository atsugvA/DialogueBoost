using System;
using System.IO;
using Jellyfin.Plugin.DialogueBoost.State;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// The rule behind every number the page shows, against real files on disk.
/// </summary>
public class WorkForecasterTests : IDisposable
{
    private const string CurrentSettings = "settings-now";

    private readonly string _dir;
    private readonly string _source;
    private readonly string _sidecar;

    public WorkForecasterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"db_forecast_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);

        _source = Path.Combine(_dir, "show.s01e01.mkv");
        _sidecar = Path.Combine(_dir, "show.s01e01.Dialogue Boost.mka");
        File.WriteAllText(_source, "source bytes");
        File.WriteAllText(_sidecar, "sidecar bytes");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A temp directory that will not delete is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A record that matches the files on disk exactly: nothing outstanding.
    /// </summary>
    private ProcessedItemRecord UpToDateRecord()
    {
        var file = new FileInfo(_source);

        return new ProcessedItemRecord
        {
            ItemId = "item",
            ProfileId = "dialogue-boost",
            SourcePath = _source,
            SourceMTimeUtc = file.LastWriteTimeUtc.Ticks,
            SourceSizeBytes = file.Length,
            ParamsHash = "full",
            ProfileParamsHash = CurrentSettings,
            SidecarPath = _sidecar,
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };
    }

    [Fact]
    public void NoRecord_IsNeverProcessed()
    {
        Assert.Equal(
            OutstandingReason.NeverProcessed,
            WorkForecaster.Classify(_source, null, CurrentSettings));
    }

    [Fact]
    public void UpToDateRecord_IsNotOutstanding()
    {
        Assert.Null(WorkForecaster.Classify(_source, UpToDateRecord(), CurrentSettings));
    }

    [Fact]
    public void FailedRecord_IsLastRunFailed()
    {
        var record = UpToDateRecord();
        record.Status = "Failed";

        Assert.Equal(
            OutstandingReason.LastRunFailed,
            WorkForecaster.Classify(_source, record, CurrentSettings));
    }

    [Fact]
    public void DeletedSidecar_IsSidecarMissing()
    {
        var record = UpToDateRecord();
        File.Delete(_sidecar);

        Assert.Equal(
            OutstandingReason.SidecarMissing,
            WorkForecaster.Classify(_source, record, CurrentSettings));
    }

    [Fact]
    public void ResizedSource_IsSourceChanged()
    {
        var record = UpToDateRecord();
        record.SourceSizeBytes += 1;

        Assert.Equal(
            OutstandingReason.SourceChanged,
            WorkForecaster.Classify(_source, record, CurrentSettings));
    }

    [Fact]
    public void RewrittenSource_IsSourceChanged()
    {
        var record = UpToDateRecord();
        record.SourceMTimeUtc -= TimeSpan.TicksPerHour;

        Assert.Equal(
            OutstandingReason.SourceChanged,
            WorkForecaster.Classify(_source, record, CurrentSettings));
    }

    [Fact]
    public void ChangedProfileSettings_IsSettingsChanged()
    {
        var record = UpToDateRecord();

        Assert.Equal(
            OutstandingReason.SettingsChanged,
            WorkForecaster.Classify(_source, record, "settings-after-the-user-raised-the-gain"));
    }

    /// <summary>
    /// The 35 records already on the dev server have no settings hash. They were written, they
    /// succeeded, and the file is there — so they count as done rather than being re-encoded on
    /// sight. The forecast reports how many are in this state separately.
    /// </summary>
    [Fact]
    public void RecordFromBeforeTheSettingsHash_CountsAsWritten()
    {
        var record = UpToDateRecord();
        record.ProfileParamsHash = null;

        Assert.Null(WorkForecaster.Classify(_source, record, CurrentSettings));
    }

    /// <summary>
    /// A missing file is the more surprising fact, so it is the one reported.
    /// </summary>
    [Fact]
    public void MissingSidecar_OutranksChangedSettings()
    {
        var record = UpToDateRecord();
        File.Delete(_sidecar);

        Assert.Equal(
            OutstandingReason.SidecarMissing,
            WorkForecaster.Classify(_source, record, "something-else"));
    }

    /// <summary>
    /// A renamed source is handled by the run itself; the forecast falls back to the recorded path
    /// so a library entry with no path does not read as work outstanding.
    /// </summary>
    [Fact]
    public void EmptyLibraryPath_FallsBackToTheRecordedPath()
    {
        Assert.Null(WorkForecaster.Classify(null, UpToDateRecord(), CurrentSettings));
    }

    /// <summary>
    /// A source that is gone entirely is not work a run could do: it is a stale record, which the
    /// cleanup task owns.
    /// </summary>
    [Fact]
    public void VanishedSource_IsNotCountedAsOutstanding()
    {
        var record = UpToDateRecord();
        File.Delete(_source);

        Assert.Null(WorkForecaster.Classify(_source, record, CurrentSettings));
    }

    /* ── what a run would actually do ──────────────────────────────────────────────────────── */

    /// <summary>
    /// The case that was on the user's screen: four episodes everybody has finished, their sidecars
    /// deleted by the cleanup and their records with them. Classified alone they are work; the gate
    /// refuses them before it looks at anything, so the band said "4 left" under a note reading
    /// "at the current settings" and the number could never go down.
    /// </summary>
    [Fact]
    public void WatchedAndNothingWritten_IsSkippedNotOutstanding()
    {
        var verdict = WorkForecaster.Verdict(
            _source, record: null, CurrentSettings, isWatched: true, skipWatchedItems: true);

        Assert.Equal(SkipReason.Watched, verdict.Skipped);
        Assert.Null(verdict.Outstanding);
        Assert.False(verdict.IsWritten);
    }

    /// <summary>
    /// The same four with the setting off: a run would write them, so they are work again. The
    /// forecast reads the setting rather than assuming the safe answer.
    /// </summary>
    [Fact]
    public void WatchedButNotSkipping_IsOutstanding()
    {
        var verdict = WorkForecaster.Verdict(
            _source, record: null, CurrentSettings, isWatched: true, skipWatchedItems: false);

        Assert.Equal(OutstandingReason.NeverProcessed, verdict.Outstanding);
        Assert.Null(verdict.Skipped);
    }

    /// <summary>
    /// Watched and already written is <em>written</em>. The file is there and it plays; the gate's
    /// refusal to make another one changes nothing about it, and counting it as skipped would take
    /// a track off the tally that is sitting on disk.
    /// </summary>
    [Fact]
    public void WatchedButAlreadyWritten_StaysWritten()
    {
        var verdict = WorkForecaster.Verdict(
            _source, UpToDateRecord(), CurrentSettings, isWatched: true, skipWatchedItems: true);

        Assert.True(verdict.IsWritten);
        Assert.Null(verdict.Skipped);
    }

    /// <summary>
    /// Nobody has watched it: the ordinary case, and the one the reason codes were written for.
    /// </summary>
    [Fact]
    public void NotWatched_KeepsItsOutstandingReason()
    {
        var record = UpToDateRecord();
        File.Delete(_sidecar);

        var verdict = WorkForecaster.Verdict(
            _source, record, CurrentSettings, isWatched: false, skipWatchedItems: true);

        Assert.Equal(OutstandingReason.SidecarMissing, verdict.Outstanding);
        Assert.Null(verdict.Skipped);
    }

    /* ── what the run wrote down about finding nothing ─────────────────────────────────────── */

    /// <summary>A record of "nothing here to encode" that has not aged.</summary>
    private ProcessedItemRecord NothingToProcessRecord()
    {
        var record = UpToDateRecord();
        record.Status = ProcessedItemRecord.NothingToProcess;
        record.SidecarPath = string.Empty;
        record.SkipReason = "no audio track matches the track rules";
        return record;
    }

    /// <summary>
    /// The run looked, found nothing to encode, and said so. Read as work outstanding it would be
    /// re-derived on every page load and declined again on every run, for ever.
    /// </summary>
    [Fact]
    public void NothingToProcessRecord_IsSkippedNotOutstanding()
    {
        var verdict = WorkForecaster.Verdict(
            _source, NothingToProcessRecord(), CurrentSettings, isWatched: false, skipWatchedItems: true);

        Assert.Equal(SkipReason.NothingToProcess, verdict.Skipped);
        Assert.Null(verdict.Outstanding);
    }

    /// <summary>
    /// It ages by the same rule a written track ages by: a setting that moves means the answer was
    /// about settings that no longer stand, so the item comes back round.
    /// </summary>
    [Fact]
    public void NothingToProcessRecord_AgesWhenTheSettingsMove()
    {
        var verdict = WorkForecaster.Verdict(
            _source, NothingToProcessRecord(), "settings-with-another-language", isWatched: false, skipWatchedItems: true);

        Assert.Equal(OutstandingReason.SettingsChanged, verdict.Outstanding);
        Assert.Null(verdict.Skipped);
    }

    [Fact]
    public void NothingToProcessRecord_AgesWhenTheSourceMoves()
    {
        var record = NothingToProcessRecord();
        record.SourceSizeBytes += 1;

        var verdict = WorkForecaster.Verdict(
            _source, record, CurrentSettings, isWatched: false, skipWatchedItems: true);

        Assert.Equal(OutstandingReason.SourceChanged, verdict.Outstanding);
    }

    /// <summary>
    /// An aged one on a watched item is still not work the run would do — the gate refuses it
    /// before it gets as far as looking at the tracks again.
    /// </summary>
    [Fact]
    public void AgedNothingToProcessRecord_OnAWatchedItem_IsStillSkipped()
    {
        var verdict = WorkForecaster.Verdict(
            _source, NothingToProcessRecord(), "another-setting", isWatched: true, skipWatchedItems: true);

        Assert.Equal(SkipReason.Watched, verdict.Skipped);
    }
}
