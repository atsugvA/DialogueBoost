using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.State;

/// <summary>
/// Why one covered item still owes a track for one profile.
/// </summary>
public enum OutstandingReason
{
    /// <summary>No record for this item and profile: it has never been processed.</summary>
    NeverProcessed,

    /// <summary>A successful record exists, but the sidecar it names is not on disk.</summary>
    SidecarMissing,

    /// <summary>The source file's size or modification time has moved since the record was written.</summary>
    SourceChanged,

    /// <summary>The profile's parameters have changed since the record was written.</summary>
    SettingsChanged,

    /// <summary>The last attempt did not succeed.</summary>
    LastRunFailed
}

/// <summary>
/// Why a run would leave one covered item alone, however its records read.
/// </summary>
/// <remarks>
/// The distinction that makes the page honest. <see cref="OutstandingReason"/> answers "is there a
/// current sidecar?"; this answers "would the next run write one?", which is
/// <c>Processing.ItemGate</c>'s question and a different one. Four episodes with no sidecar and no
/// record read as four outstanding tracks, while the gate refuses all four before it looks at
/// anything — so the count never moved and the words under it ("at the current settings") were the
/// part that was actually false.
/// </remarks>
public enum SkipReason
{
    /// <summary>
    /// Every account that counts has played it, and <c>SkipWatchedItems</c> is on. The same
    /// predicate the gate uses — <c>Processing.WatchedItems</c> — not a second reading of it.
    /// </summary>
    Watched,

    /// <summary>
    /// A run looked and found nothing to encode: no track matched the rules, or none is in a
    /// language this profile processes. The run's own answer, read back — the page cannot reach it
    /// any other way without probing every source on every load.
    /// </summary>
    NothingToProcess
}

/// <summary>
/// What the selection still owes the library, as of now.
/// </summary>
/// <param name="CoveredItems">Distinct processable videos the stored scopes resolve to.</param>
/// <param name="EnabledProfiles">How many profiles are switched on.</param>
/// <param name="ExpectedTracks">
/// One track per covered item per enabled profile — the work a run would have to have finished for
/// there to be nothing left to do.
/// </param>
/// <param name="WrittenTracks">
/// Tracks that exist on disk, at the current settings, against an unchanged source. These are the
/// ones a run would find already done and skip.
/// </param>
/// <param name="SkippedTracks">
/// Tracks the next run would decline to write, at the settings standing now. Not written and not
/// outstanding — counting them as either one makes the page lie in one direction or the other.
/// </param>
/// <param name="Skipped">The skipped count broken down by reason.</param>
/// <param name="OutstandingTracks">
/// <paramref name="ExpectedTracks"/> minus <paramref name="WrittenTracks"/> and
/// <paramref name="SkippedTracks"/>. Work the next run will actually do.
/// </param>
/// <param name="Outstanding">The outstanding count broken down by reason.</param>
/// <param name="UnverifiedTracks">
/// Written tracks whose record predates the stored settings hash, so a settings change cannot be
/// ruled out for them. They are counted as written — the file is there and it succeeded — and the
/// next run that touches them records enough to answer properly.
/// </param>
public sealed record WorkForecast(
    int CoveredItems,
    int EnabledProfiles,
    int ExpectedTracks,
    int WrittenTracks,
    int SkippedTracks,
    int OutstandingTracks,
    IReadOnlyDictionary<SkipReason, int> Skipped,
    IReadOnlyDictionary<OutstandingReason, int> Outstanding,
    int UnverifiedTracks)
{
    /// <summary>
    /// Gets an empty forecast, for when nothing is selected or no profile is on.
    /// </summary>
    public static WorkForecast Empty { get; } = new(
        CoveredItems: 0,
        EnabledProfiles: 0,
        ExpectedTracks: 0,
        WrittenTracks: 0,
        SkippedTracks: 0,
        OutstandingTracks: 0,
        Skipped: new Dictionary<SkipReason, int>(),
        Outstanding: new Dictionary<OutstandingReason, int>(),
        UnverifiedTracks: 0);
}
