using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.State;

/// <summary>
/// Answers "how much is left to do?" without running anything.
/// </summary>
/// <remarks>
/// <para>
/// The page has to be able to say <c>38 written · 4 outstanding</c> on load, and a number that is
/// only true sometimes is worse than no number. A run gets its answer by probing every source and
/// comparing the full <c>ParamsHash</c>; that is far too expensive to do on a page load, so this
/// reaches the same answer a cheaper way: an item whose file has not moved has the same audio
/// tracks, so comparing the stored settings hash is enough to know whether its sidecar is current.
/// </para>
/// <para>
/// Cost is one query per selected scope, one per enabled profile, and two file checks per expected
/// track. That is why it is its own endpoint rather than part of <c>GET /Selection</c>.
/// </para>
/// <para>
/// It borrows <see cref="WatchedItems"/> from <c>Processing</c> deliberately. "Would the next run
/// write this?" is <c>ItemGate</c>'s question, and answering it here with a second reading of the
/// same setting is how the two drift apart. One predicate, asked
/// from wherever the answer is needed.
/// </para>
/// </remarks>
public sealed class WorkForecaster
{
    private readonly SelectionStore _store;
    private readonly ScopeResolver _resolver;
    private readonly ProcessingStateRepository _state;
    private readonly WatchedItems _watched;
    private readonly ILogger<WorkForecaster> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkForecaster"/> class.
    /// </summary>
    public WorkForecaster(
        SelectionStore store,
        ScopeResolver resolver,
        ProcessingStateRepository state,
        WatchedItems watched,
        ILogger<WorkForecaster> logger)
    {
        _store = store;
        _resolver = resolver;
        _state = state;
        _watched = watched;
        _logger = logger;
    }

    /// <summary>
    /// Counts what the current selection covers, what has already been written for it, and what is
    /// still outstanding.
    /// </summary>
    public async Task<WorkForecast> ForecastAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var profiles = config.GetAllProfiles()
            .Where(profile => profile.Enabled)
            .ToList();

        var selection = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        var items = _resolver.ResolveScopes(selection)
            .SelectMany(scope => scope.Items)
            .DistinctBy(item => item.Id)
            .ToList();

        if (items.Count == 0 || profiles.Count == 0)
        {
            return WorkForecast.Empty with
            {
                CoveredItems = items.Count,
                EnabledProfiles = profiles.Count
            };
        }

        // Once per item, not once per track: the gate decides before any profile is considered,
        // and asking the user-data store the same question five times would be five times the cost
        // for the same answer. Once per *account*, in fact — the per-item form is a round trip to
        // the user-data store each, and this endpoint asks about everything the selection covers.
        var watched = _watched.WatchedAmong(items, config);

        var outstanding = new Dictionary<OutstandingReason, int>();
        var skipped = new Dictionary<SkipReason, int>();
        int written = 0;
        int unverified = 0;

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string expectedSettings = ProcessingStateRepository.ComputeProfileParamsHash(
                profile, config.ClaimsDefaultTrack(profile));
            var records = (await _state.GetRecordsByProfileAsync(profile.Id).ConfigureAwait(false))
                .ToDictionary(record => record.ItemId, StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                records.TryGetValue(item.Id.ToString("N"), out var record);
                var verdict = Verdict(
                    item.Path, record, expectedSettings, watched.Contains(item.Id), config.SkipWatchedItems);

                if (verdict.IsWritten)
                {
                    written++;
                    if (string.IsNullOrEmpty(record!.ProfileParamsHash))
                    {
                        unverified++;
                    }
                }
                else if (verdict.Skipped is { } skip)
                {
                    skipped[skip] = skipped.GetValueOrDefault(skip) + 1;
                }
                else
                {
                    var reason = verdict.Outstanding!.Value;
                    outstanding[reason] = outstanding.GetValueOrDefault(reason) + 1;
                }
            }
        }

        int expected = items.Count * profiles.Count;
        int skippedTotal = skipped.Values.Sum();

        _logger.LogDebug(
            "DialogueBoost: {Covered} covered item(s) × {Profiles} profile(s) = {Expected} track(s); {Written} written, {Skipped} skipped, {Outstanding} outstanding.",
            items.Count,
            profiles.Count,
            expected,
            written,
            skippedTotal,
            expected - written - skippedTotal);

        return new WorkForecast(
            CoveredItems: items.Count,
            EnabledProfiles: profiles.Count,
            ExpectedTracks: expected,
            WrittenTracks: written,
            SkippedTracks: skippedTotal,
            OutstandingTracks: expected - written - skippedTotal,
            Skipped: skipped,
            Outstanding: outstanding,
            UnverifiedTracks: unverified);
    }

    /// <summary>
    /// What the next run would do about one item and profile: find it done, decline it, or write it.
    /// </summary>
    /// <remarks>
    /// <see cref="Classify"/> reads the record and the files; this adds what the gate would decide
    /// before either is looked at. The order is the point — a watched item that already *has* a
    /// current sidecar is written, not skipped: the file is there and it plays, and the gate's
    /// refusal to make another one changes nothing about it. Only when there is work to do does it
    /// matter whether the run would do it.
    /// </remarks>
    /// <param name="sourcePath">Where the library says the source is now; may be empty.</param>
    /// <param name="record">The stored record for this item and profile, if any.</param>
    /// <param name="expectedSettings">The profile's current settings hash.</param>
    /// <param name="isWatched">Whether every account that counts has played it.</param>
    /// <param name="skipWatchedItems">The configuration's <c>SkipWatchedItems</c>.</param>
    public static TrackVerdict Verdict(
        string? sourcePath,
        ProcessedItemRecord? record,
        string expectedSettings,
        bool isWatched,
        bool skipWatchedItems)
    {
        // A run that looked and found nothing to encode said so. Re-deriving that as work
        // outstanding is how the page comes to disagree with the run permanently.
        if (record is not null && record.Status == ProcessedItemRecord.NothingToProcess)
        {
            var stale = Aged(sourcePath, record, expectedSettings);
            return stale is null
                ? TrackVerdict.Skip(SkipReason.NothingToProcess)
                : Decide(stale.Value, isWatched, skipWatchedItems);
        }

        var outstanding = Classify(sourcePath, record, expectedSettings);
        return outstanding is null
            ? TrackVerdict.Written
            : Decide(outstanding.Value, isWatched, skipWatchedItems);
    }

    /// <summary>
    /// There is work here — unless the gate would refuse the item before doing any of it.
    /// </summary>
    private static TrackVerdict Decide(OutstandingReason reason, bool isWatched, bool skipWatchedItems) =>
        skipWatchedItems && isWatched
            ? TrackVerdict.Skip(SkipReason.Watched)
            : TrackVerdict.Work(reason);

    /// <summary>
    /// Why one item still owes a track for one profile, or <see langword="null"/> if it does not.
    /// </summary>
    /// <remarks>
    /// Takes the source path rather than the library item so the rule behind every number on the
    /// page can be tested against real files, without standing up a library.
    /// The order matters: a missing sidecar is reported as missing even when the settings have also
    /// moved, because it is the more surprising fact and the one worth showing first.
    /// </remarks>
    /// <param name="sourcePath">Where the library says the source is now; may be empty.</param>
    /// <param name="record">The stored record for this item and profile, if any.</param>
    /// <param name="expectedSettings">The profile's current settings hash.</param>
    public static OutstandingReason? Classify(
        string? sourcePath,
        ProcessedItemRecord? record,
        string expectedSettings)
    {
        if (record is null)
        {
            return OutstandingReason.NeverProcessed;
        }

        if (!string.Equals(record.Status, "Success", StringComparison.OrdinalIgnoreCase))
        {
            return OutstandingReason.LastRunFailed;
        }

        if (string.IsNullOrWhiteSpace(record.SidecarPath) || !File.Exists(record.SidecarPath))
        {
            return OutstandingReason.SidecarMissing;
        }

        return Aged(sourcePath, record, expectedSettings);
    }

    /// <summary>
    /// Whether what a record says has been overtaken — by the source moving under it or by the
    /// profile's settings changing since it was written. Null when it still stands.
    /// </summary>
    /// <remarks>
    /// Shared, because it ages both kinds of record by the same rule: a written sidecar and a
    /// recorded "nothing to encode here" are both answers about a particular source at particular
    /// settings, and both stop being true for the same two reasons.
    /// </remarks>
    private static OutstandingReason? Aged(
        string? sourcePath,
        ProcessedItemRecord record,
        string expectedSettings)
    {
        if (SourceMoved(sourcePath, record))
        {
            return OutstandingReason.SourceChanged;
        }

        // A record written before the settings hash existed cannot be compared. The file is there
        // and it succeeded, so it counts as written; ForecastAsync reports how many are in this
        // state rather than quietly assuming either answer.
        if (!string.IsNullOrEmpty(record.ProfileParamsHash) &&
            !record.ProfileParamsHash.Equals(expectedSettings, StringComparison.OrdinalIgnoreCase))
        {
            return OutstandingReason.SettingsChanged;
        }

        return null;
    }

    /// <summary>
    /// Whether the source has changed under a record, which would change its track signature and
    /// so the hash a run computes.
    /// </summary>
    private static bool SourceMoved(string? sourcePath, ProcessedItemRecord record)
    {
        string path = string.IsNullOrWhiteSpace(sourcePath) ? record.SourcePath : sourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            // The source is gone. A run cannot process it either, so this is not work outstanding —
            // it is a stale record, which the cleanup task owns.
            return false;
        }

        return file.Length != record.SourceSizeBytes
            || file.LastWriteTimeUtc.Ticks != record.SourceMTimeUtc;
    }
}

/// <summary>
/// One item, one profile: written, skipped, or outstanding — exactly one of the three.
/// </summary>
/// <param name="Outstanding">Why a run would have work to do here, or null.</param>
/// <param name="Skipped">Why a run would decline, or null.</param>
public readonly record struct TrackVerdict(OutstandingReason? Outstanding, SkipReason? Skipped)
{
    /// <summary>Gets the verdict for a track that is on disk and current.</summary>
    public static TrackVerdict Written => default;

    /// <summary>Gets whether the sidecar is there and current, which is neither of the other two.</summary>
    public bool IsWritten => Outstanding is null && Skipped is null;

    /// <summary>A run would decline this one.</summary>
    public static TrackVerdict Skip(SkipReason reason) => new(null, reason);

    /// <summary>A run would write this one.</summary>
    public static TrackVerdict Work(OutstandingReason reason) => new(reason, null);
}
