using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// Where the recorded track is once the bookkeeping is done, and whether it had to be moved there.
/// </summary>
/// <param name="TrackPath">
/// The path the track belongs at — unless it could not be moved there, in which case it is still
/// where it was.
/// </param>
/// <param name="Moved">Whether a track was moved, so Jellyfin has to read the item again.</param>
public readonly record struct Reconciled(string TrackPath, bool Moved);

/// <summary>
/// Brings what is on disk back in line with what the record says, before anything decides whether
/// an encode is needed.
/// </summary>
/// <remarks>
/// Three cases, one mistake seen from three sides: the record names a path that is no longer where
/// the track belongs. Renaming the video moves where it belongs; changing
/// <see cref="Configuration.SidecarLocation"/> changes the folder; renaming the profile's marker or
/// handing the default claim to another profile changes what it is called. The first two are the
/// same track somewhere else, and are moved rather than encoded again. The third is not the same
/// file any more — the encode after this writes the one that belongs — and is deleted, or it would
/// stay in the audio menu for ever.
/// </remarks>
public sealed class SidecarBookkeeping
{
    private readonly ProcessingStateRepository _stateRepository;
    private readonly ILogger<SidecarBookkeeping> _logger;

    public SidecarBookkeeping(ProcessingStateRepository stateRepository, ILogger<SidecarBookkeeping> logger)
    {
        _stateRepository = stateRepository;
        _logger = logger;
    }

    public async Task<Reconciled> ReconcileAsync(
        ProcessedItemRecord existing, string itemId, string sourcePath, string sidecarPath, bool dryRun)
    {
        // Ordinal, not culture-aware: on Linux two paths differing only in case are two files.
        bool sourceMoved = !existing.SourcePath.Equals(sourcePath, StringComparison.Ordinal);
        string recorded = existing.SidecarPath;
        bool elsewhere = !string.IsNullOrWhiteSpace(recorded)
            && !recorded.Equals(sidecarPath, StringComparison.Ordinal)
            && File.Exists(recorded);

        // The same track somewhere else, as against a track under a name nobody writes any more.
        bool sameTrack = sourceMoved
            || Path.GetFileName(recorded).Equals(Path.GetFileName(sidecarPath), StringComparison.Ordinal);

        if (dryRun)
        {
            // A dry run decides everything and writes nothing, and moving or deleting a track is
            // writing. Both used to happen here regardless, because this runs before the run looks
            // at the setting.
            if (elsewhere && sameTrack)
            {
                _logger.LogInformation("[DryRun] Would move '{OldPath:l}' to '{NewPath:l}'.", recorded, sidecarPath);
                return new Reconciled(recorded, Moved: false);
            }

            if (elsewhere)
            {
                _logger.LogInformation(
                    "[DryRun] Would delete '{OldPath:l}', which '{NewPath:l}' replaces.", recorded, sidecarPath);
            }

            return new Reconciled(sidecarPath, Moved: false);
        }

        if (sourceMoved)
        {
            _logger.LogInformation("Source moved from {OldPath} to {NewPath}; following it.", existing.SourcePath, sourcePath);
            await _stateRepository.UpdateSourcePathIfRenamedAsync(itemId, sourcePath).ConfigureAwait(false);
        }

        if (!elsewhere)
        {
            return new Reconciled(sidecarPath, Moved: false);
        }

        if (sameTrack)
        {
            if (Relocate(recorded, sidecarPath))
            {
                // The record follows the file. It used to go on naming the old path, so the band
                // counted the moved track as missing for as long as the record lived.
                await _stateRepository.UpdateSidecarPathAsync(itemId, existing.ProfileId, sidecarPath).ConfigureAwait(false);
                existing.SidecarPath = sidecarPath;
                return new Reconciled(sidecarPath, Moved: true);
            }

            // Between the two folders Jellyfin reads, a track that could not be moved is still
            // exactly the track it was — listed, played, current — so it stays, and the next run
            // tries again. Behind a source that moved it is not: Jellyfin matches a track by the
            // video's own name, so that one goes, and the encode writes it where it belongs.
            if (!sourceMoved)
            {
                return new Reconciled(recorded, Moved: false);
            }
        }

        Delete(recorded);
        return new Reconciled(sidecarPath, Moved: false);
    }

    /// <summary>
    /// Deletes copies of a track that was just published, left in a folder it no longer belongs in.
    /// </summary>
    /// <remarks>
    /// One profile, one track per item: a second copy is a second row in the audio menu. The
    /// bookkeeping above moves what a record names, so this only finds work where the record was
    /// forgotten across a change of location and the track was written again from scratch.
    /// </remarks>
    public void RemoveCopies(IEnumerable<string> copies)
    {
        foreach (var copy in copies)
        {
            if (!File.Exists(copy))
            {
                continue;
            }

            try
            {
                File.Delete(copy);
                _logger.LogInformation("Deleted a copy of the track left in the other folder: {Path}", copy);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete the copy of the track at {Path}.", copy);
            }
        }
    }

    /// <summary>
    /// Moves a track to where it belongs without its final name ever holding a partial file.
    /// </summary>
    /// <remarks>
    /// The two folders are often on two disks — the media on one, Jellyfin's own data on another —
    /// and across disks <see cref="File.Move(string, string)"/> is a copy and a delete made straight
    /// into the destination name: cut short, it leaves a truncated file that Jellyfin lists as the
    /// track and the next run takes for a finished one. So the track goes to a temporary name beside
    /// its destination first — a rename on one disk, and across two a copy whose original is removed
    /// only once the copy is complete — and is renamed into place from there. A copy already at the
    /// destination is replaced: the record vouches for the one being moved, not for that one.
    /// </remarks>
    private bool Relocate(string from, string to)
    {
        string temp = SidecarNamer.TempPathBeside(to);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to) ?? string.Empty);
            File.Move(from, temp);
            File.Move(temp, to, overwrite: true);
            _logger.LogInformation("Moved the track {OldPath} to {NewPath}", from, to);
            return true;
        }
        catch (Exception ex)
        {
            PutBack(temp, from);
            _logger.LogWarning(ex, "Could not move the track {OldPath} to {NewPath}.", from, to);
            return false;
        }
    }

    /// <summary>
    /// After a failed move: a temporary copy beside an intact original is a partial one and goes; a
    /// temporary copy whose original is already gone is the track, and goes back.
    /// </summary>
    private void PutBack(string temp, string original)
    {
        try
        {
            if (!File.Exists(temp))
            {
                return;
            }

            if (File.Exists(original))
            {
                File.Delete(temp);
            }
            else
            {
                File.Move(temp, original);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not put back {TempPath} as {OldPath}.", temp, original);
        }
    }

    private void Delete(string stalePath)
    {
        try
        {
            File.Delete(stalePath);
            _logger.LogInformation("Deleted the sidecar left at the old name: {OldPath}", stalePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the sidecar left at {OldPath}.", stalePath);
        }
    }
}
