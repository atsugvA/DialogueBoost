using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// One item, every enabled profile: decide, encode, publish, record.
/// </summary>
/// <remarks>
/// Orchestration only. Whether the item is worth touching is <see cref="ItemGate"/>'s answer, which
/// tracks qualify is <see cref="CandidateStreams"/>', and putting the files back where the record
/// says they are is <see cref="SidecarBookkeeping"/>'s. What is left here is the sequence and the
/// decision each profile needs: already done, dry run, refused by the volume, or work.
/// </remarks>
public class ItemProcessor
{
    private readonly ItemGate _gate;
    private readonly CandidateStreams _candidates;
    private readonly SidecarBookkeeping _bookkeeping;
    private readonly SidecarWriter _writer;
    private readonly StorageProbe _storage;
    private readonly IMetadataRefresher _refresher;
    private readonly ProcessingStateRepository _stateRepository;
    private readonly JobConcurrencyManager _concurrencyManager;
    private readonly ILogger<ItemProcessor> _logger;

    public ItemProcessor(
        ItemGate gate,
        CandidateStreams candidates,
        SidecarBookkeeping bookkeeping,
        SidecarWriter writer,
        StorageProbe storage,
        IMetadataRefresher refresher,
        ProcessingStateRepository stateRepository,
        JobConcurrencyManager concurrencyManager,
        ILogger<ItemProcessor> logger)
    {
        _gate = gate;
        _candidates = candidates;
        _bookkeeping = bookkeeping;
        _writer = writer;
        _storage = storage;
        _refresher = refresher;
        _stateRepository = stateRepository;
        _concurrencyManager = concurrencyManager;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ItemProcessingResult>> ProcessItemAsync(
        BaseItem item,
        string? specificProfileId,
        CancellationToken cancellationToken,
        bool overrideWatched = false)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var gate = await _gate.CheckAsync(item, config, overrideWatched, cancellationToken).ConfigureAwait(false);
        if (!gate.Proceed)
        {
            // Counted, not recorded. A run over 284 items that declined 255 of them at the gate
            // used to return nothing for each and report "skipped 0" — the same arithmetic that
            // made a run which encoded nothing say "Processed: 28". One decision per item here,
            // because the gate answers per item and never reaches a profile.
            return new[] { new ItemProcessingResult(ProcessingOutcome.Skipped, null, gate.Reason) };
        }

        string sourcePath = item.Path;
        var candidates = await _candidates
            .ForAsync(item, sourcePath, config.TrackSelectionRules, cancellationToken)
            .ConfigureAwait(false);

        var profiles = config.GetAllProfiles()
            .Where(profile => profile.Enabled)
            .Where(profile => string.IsNullOrWhiteSpace(specificProfileId)
                || profile.Id.Equals(specificProfileId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A file whose tracks could not be read is not a file with nothing in it. Nothing durable
        // is recorded for that one — it is a broken file or a folder that refused us, and the next
        // run has to look again rather than trust a note saying there was nothing here.
        if (!candidates.Readable)
        {
            return new[]
            {
                new ItemProcessingResult(
                    ProcessingOutcome.Failed, null, "the source's audio tracks could not be read")
            };
        }

        if (candidates.NothingQualifies)
        {
            return await NothingToProcessAsync(
                item, sourcePath, profiles, candidates.Streams, config,
                "no audio track matches the track rules").ConfigureAwait(false);
        }

        var results = new List<ItemProcessingResult>(profiles.Count);
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ProcessProfileAsync(
                item, sourcePath, profile, candidates.Streams, config, gate, overrideWatched, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Writes down that there is nothing here to encode, for each profile that asked.
    /// </summary>
    /// <remarks>
    /// The run used to return an empty list and keep the answer to itself, so the page re-derived
    /// the item as work outstanding on every load, for ever, and the run declined it again on every
    /// pass. The record ages on its own: it carries the source's size and mtime and the profile's
    /// settings hash, so a source or a setting that moves brings the item back round by the same
    /// rule that brings a written one back.
    /// </remarks>
    private async Task<IReadOnlyList<ItemProcessingResult>> NothingToProcessAsync(
        BaseItem item,
        string sourcePath,
        List<BaseProfileConfig> profiles,
        List<AudioStreamInfo> streams,
        PluginConfiguration config,
        string reason)
    {
        var results = new List<ItemProcessingResult>(profiles.Count);
        foreach (var profile in profiles)
        {
            _logger.LogInformation(
                "Nothing for the {ProfileId} profile to write for '{ItemName:l}': {Reason}.",
                profile.Id, item.Name, reason);

            var record = DraftFor(item, sourcePath, profile, streams, config.ClaimsDefaultTrack(profile))
                .Build(ProcessedItemRecord.NothingToProcess, skipReason: reason);

            results.Add(new ItemProcessingResult(
                ProcessingOutcome.Skipped, await Save(record).ConfigureAwait(false), reason));
        }

        return results;
    }

    /// <summary>The parts of a record that this item and this profile fix, however the attempt ends.</summary>
    private static RecordDraft DraftFor(
        BaseItem item,
        string sourcePath,
        BaseProfileConfig profile,
        List<AudioStreamInfo> streams,
        bool claimsDefault) =>
        new(
            item.Id.ToString("N"),
            profile.Id,
            sourcePath,
            new FileInfo(sourcePath),
            SidecarNamer.GetSidecarPath(sourcePath, profile.SidecarNamingMarker, claimsDefault),
            ProcessingStateRepository.ComputeParamsHash(profile, streams, claimsDefault),
            ProcessingStateRepository.ComputeProfileParamsHash(profile, claimsDefault));

    private async Task<ItemProcessingResult> ProcessProfileAsync(
        BaseItem item,
        string sourcePath,
        BaseProfileConfig profile,
        List<AudioStreamInfo> candidateStreams,
        PluginConfiguration config,
        GateDecision gate,
        bool overrideWatched,
        CancellationToken cancellationToken)
    {
        string itemId = item.Id.ToString("N");
        bool claimsDefault = config.ClaimsDefaultTrack(profile);
        string sidecarPath = SidecarNamer.GetSidecarPath(sourcePath, profile.SidecarNamingMarker, claimsDefault);
        var draft = DraftFor(item, sourcePath, profile, candidateStreams, claimsDefault);

        var existing = await _stateRepository.GetRecordAsync(itemId, profile.Id).ConfigureAwait(false);
        if (existing is not null)
        {
            await _bookkeeping.ReconcileAsync(existing, itemId, sourcePath, sidecarPath).ConfigureAwait(false);

            if (IsUpToDate(existing, draft.ParamsHash, sidecarPath))
            {
                _logger.LogDebug("'{ItemName:l}' already has a current {ProfileId} track.", item.Name, profile.Id);
                return new ItemProcessingResult(ProcessingOutcome.AlreadyDone, existing);
            }
        }

        // This profile may have nothing to do with a file the rules did accept: its languages can
        // match none of the tracks. That is a fact about the pair, not a failure — and it was
        // recorded as one, because BuildCommand threw and the catch below called it Failed. The
        // page then showed it as work outstanding and every run retried it.
        if (FfmpegCommandBuilder.TargetLanguages(candidateStreams, profile).Count == 0)
        {
            return (await NothingToProcessAsync(
                    item,
                    sourcePath,
                    new List<BaseProfileConfig> { profile },
                    candidateStreams,
                    config,
                    "no audio track is in a language this profile processes").ConfigureAwait(false))
                [0];
        }

        if (config.DryRun)
        {
            _logger.LogInformation(
                "[DryRun] Would write '{SidecarPath:l}' for '{ItemName:l}'.", sidecarPath, item.Name);
            return new ItemProcessingResult(
                ProcessingOutcome.Skipped, draft.Build("Skipped", skipReason: "DryRun active"), "dry run");
        }

        // Nothing is encoded into a directory that will refuse the file at the end. The probe is of
        // the item's own folder, because that is where the permission lives — a volume-level check
        // passes while the one folder that matters is denied.
        var storage = _storage.Check(Path.GetDirectoryName(sidecarPath));
        if (!storage.IsWritable)
        {
            _logger.LogError("DialogueBoost: {Explanation:l}", storage.Explain());
            return new ItemProcessingResult(
                ProcessingOutcome.Failed,
                await Save(draft.Build("Failed", skipReason: storage.Explain())).ConfigureAwait(false),
                storage.Explain());
        }

        using (await _concurrencyManager.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var command = FfmpegCommandBuilder.BuildCommand(sourcePath, candidateStreams, profile);

                string published = await _writer.ProcessAndWriteSidecarAsync(
                    sidecarPath, command, profile.VerifyBeforePublish, cancellationToken).ConfigureAwait(false);

                var record = draft.Build(
                    "Success",
                    sidecarPath: published,
                    processedLanguages: command.ProcessedLanguages.ToList(),
                    // Protected from the cleanup task in both the cases where deleting it again
                    // would undo what was just asked for: the item is already watched, so the very
                    // next cleanup would collect it; or somebody asked for this item by hand,
                    // which is what `overrideWatched` means at every call site that passes it.
                    exemptFromCleanup: gate.IsWatched || overrideWatched);

                await Save(record).ConfigureAwait(false);

                if (profile.RefreshMode == RefreshMode.PerItemImmediate)
                {
                    await _refresher.RefreshNowAsync(item, cancellationToken).ConfigureAwait(false);
                }

                return new ItemProcessingResult(ProcessingOutcome.Written, record);
            }
            catch (OperationCanceledException)
            {
                // Stopping the task is not a failure of the item. Recorded as one, it would fill
                // the history with "A task was canceled" and read as a broken file. The record it
                // already had stands, and the next run picks it up again; the temp file is cleaned
                // up on the way out by the writer.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed writing the {ProfileId} track for '{ItemName:l}'.", profile.Id, item.Name);
                return new ItemProcessingResult(
                    ProcessingOutcome.Failed,
                    await Save(draft.Build("Failed", skipReason: ex.Message)).ConfigureAwait(false),
                    ex.Message);
            }
        }
    }

    /// <summary>
    /// Whether the recorded work still stands: same parameters, same tracks, and the file is still
    /// there. The hash covers the profile's settings and a signature of the source's audio tracks,
    /// so changing either brings the item back round.
    /// </summary>
    private static bool IsUpToDate(ProcessedItemRecord existing, string paramsHash, string sidecarPath) =>
        existing.Status == "Success"
        && existing.ParamsHash.Equals(paramsHash, StringComparison.OrdinalIgnoreCase)
        && File.Exists(sidecarPath);

    private async Task<ProcessedItemRecord> Save(ProcessedItemRecord record)
    {
        await _stateRepository.SaveRecordAsync(record).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// The parts of a record that are the same however the attempt ends, so the four ways it can
    /// end differ only in what actually differs.
    /// </summary>
    private sealed record RecordDraft(
        string ItemId,
        string ProfileId,
        string SourcePath,
        FileInfo Source,
        string SidecarPath,
        string ParamsHash,
        string ProfileParamsHash)
    {
        public ProcessedItemRecord Build(
            string status,
            string? skipReason = null,
            string? sidecarPath = null,
            List<string>? processedLanguages = null,
            bool exemptFromCleanup = false) =>
            new()
            {
                ItemId = ItemId,
                ProfileId = ProfileId,
                SourcePath = SourcePath,
                SourceMTimeUtc = Source.LastWriteTimeUtc.Ticks,
                SourceSizeBytes = Source.Length,
                ParamsHash = ParamsHash,
                ProfileParamsHash = ProfileParamsHash,
                SidecarPath = sidecarPath ?? SidecarPath,
                ProcessedLanguages = processedLanguages ?? new List<string>(),
                Status = status,
                SkipReason = skipReason,
                ProcessedAtUtc = DateTime.UtcNow.Ticks,
                ExemptFromCleanup = exemptFromCleanup
            };
    }
}
