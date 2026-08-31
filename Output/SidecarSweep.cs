using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Output;

/// <summary>
/// Every file this plugin has written, found so that all of it can be taken away again.
/// </summary>
/// <remarks>
/// The way *in* is granular by design — a profile at a time, a watched item at a time, one track
/// at a time. The way *out* cannot be, because a user who has decided to remove the plugin should
/// not have to reason about which of those buttons together add up to "everything", and because
/// the answer is not the same as any of them: <c>DeleteByProfile</c> can only delete what a record
/// names, and a record can be gone while its file is not.
/// <para>
/// So the sweep asks twice, and takes the union.
/// </para>
/// <para>
/// <b>Wherever every item in every library could have one.</b> A sidecar's name is a pure function
/// of the source path and the profile's marker (<see cref="SidecarNamer"/>), so for every video item
/// the library holds there are exactly two names per profile — claimed and unclaimed — in each of
/// the two folders a track can be in (<see cref="SidecarPlacement"/>): beside the item, and in its
/// metadata folder. Both are looked in whatever the setting says now, because it may have said the
/// other one when the track was written. Read by listing each directory once rather than testing
/// each name: a release folder holds a whole season, so this is one <c>readdir</c> where it would
/// otherwise be ten <c>stat</c>s an episode.
/// </para>
/// <para>
/// <b>And whatever the records name.</b> That is the only thing that finds a file written under a
/// marker nobody uses any more, or one beside an item that has since left the library. Live
/// example while this was written: 26 of 83 records pointed at files whose fixture library had
/// been deleted, and one pointed at a folder that no longer existed at all.
/// </para>
/// <para>
/// What neither can find is a file whose marker was changed <em>and</em> whose record was
/// forgotten. Nothing on disk identifies it — the plugin writes no marker of its own into the
/// container — so it is left alone rather than guessed at. A hand-made <c>.mka</c> beside a source
/// is a real thing in a real library and must survive this.
/// </para>
/// </remarks>
public sealed class SidecarSweep
{
    private readonly IMediaLibrary _library;
    private readonly SidecarPlacement _placement;
    private readonly ProcessingStateRepository _state;
    private readonly ILogger<SidecarSweep> _logger;

    public SidecarSweep(
        IMediaLibrary library,
        SidecarPlacement placement,
        ProcessingStateRepository state,
        ILogger<SidecarSweep> logger)
    {
        _library = library;
        _placement = placement;
        _state = state;
        _logger = logger;
    }

    /// <summary>What a sweep found, and what became of it.</summary>
    /// <param name="Files">Paths of every sidecar found.</param>
    /// <param name="Items">The library items those sidecars sit beside, for a refresh.</param>
    public sealed record Found(IReadOnlyList<string> Files, IReadOnlyList<BaseItem> Items)
    {
        public int Folders => Files
            .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    /// <summary>
    /// Every sidecar this plugin can still account for, without deleting anything.
    /// </summary>
    /// <remarks>
    /// Separated from the deletion so the page can put a real number in its confirmation. A
    /// question that names the count is worth more than a second dialog that does not.
    /// </remarks>
    public async Task<Found> FindAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var markers = config.GetAllProfiles()
            .Select(profile => profile.SidecarNamingMarker)
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var files = new HashSet<string>(StringComparer.Ordinal);
        var items = new Dictionary<Guid, BaseItem>();

        FindWhereverWritten(markers, files, items, cancellationToken);
        await FindByRecordAsync(files, items).ConfigureAwait(false);

        return new Found(files.ToList(), items.Values.ToList());
    }

    /// <summary>
    /// Deletes what <see cref="FindAsync"/> found, and clears every record.
    /// </summary>
    /// <returns>How many files were deleted and how many records were cleared.</returns>
    public async Task<(int FilesDeleted, int FilesFailed, int RecordsCleared)> RemoveAsync(
        Found found,
        CancellationToken cancellationToken)
    {
        int deleted = 0;
        int failed = 0;

        foreach (var path in found.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "DialogueBoost: could not delete the track at {Path}", path);
            }
        }

        // Cleared whatever happened to the files: a record whose file could not be deleted is not a
        // record worth keeping either, and leaving it would report the track as written.
        int records = await _state.DeleteAllRecordsAsync().ConfigureAwait(false);

        _logger.LogInformation(
            "DialogueBoost: removed {Deleted} of {Found} track(s) and cleared {Records} record(s).",
            deleted,
            found.Files.Count,
            records);

        return (deleted, failed, records);
    }

    /// <summary>
    /// The names this plugin would write for every item the library holds, in both of the folders
    /// they can be in, listed a directory at a time.
    /// </summary>
    private void FindWhereverWritten(
        IReadOnlyList<string> markers,
        HashSet<string> files,
        Dictionary<Guid, BaseItem> items,
        CancellationToken cancellationToken)
    {
        // directory → the sidecar names it could hold → the item each belongs to.
        var expected = new Dictionary<string, Dictionary<string, BaseItem>>(StringComparer.Ordinal);

        foreach (var (item, directory) in EveryItemFolder(cancellationToken))
        {
            if (!expected.TryGetValue(directory, out var names))
            {
                names = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
                expected[directory] = names;
            }

            foreach (var marker in markers)
            {
                foreach (var name in SidecarNamer.CandidateNames(item.Path, marker))
                {
                    names[name] = item;
                }
            }
        }

        foreach (var (directory, names) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var path in Listed(directory, "*.mka"))
            {
                if (names.TryGetValue(Path.GetFileName(path), out var item))
                {
                    files.Add(path);
                    items[item.Id] = item;
                }
            }
        }
    }

    /// <summary>
    /// Deletes the temporary files that encodes interrupted by a crash or a hard stop left in the
    /// folders this plugin writes into.
    /// </summary>
    /// <param name="writtenBeforeUtc">
    /// Only files last written before this moment are touched — anything newer belongs to a run that
    /// is still writing it.
    /// </param>
    /// <returns>How many were deleted.</returns>
    public int RemoveLeftoverTemps(DateTime writtenBeforeUtc, CancellationToken cancellationToken)
    {
        int removed = 0;
        var folders = EveryItemFolder(cancellationToken)
            .Select(found => found.Folder)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var directory in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var path in Listed(directory, "*.mka"))
            {
                if (!SidecarNamer.IsTemporary(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= writtenBeforeUtc)
                    {
                        continue;
                    }

                    File.Delete(path);
                    removed++;
                    _logger.LogInformation("DialogueBoost: removed {Path}, left behind by an encode that never finished.", path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DialogueBoost: could not remove {Path}", path);
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Every video item the libraries hold that has a file, once for each folder its tracks could
    /// be in.
    /// </summary>
    private IEnumerable<(BaseItem Item, string Folder)> EveryItemFolder(CancellationToken cancellationToken)
    {
        foreach (var library in _library.Libraries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in _library.Within(library, ProcessableItem.Kinds))
            {
                foreach (var folder in _placement.FoldersOf(item))
                {
                    yield return (item, folder);
                }
            }
        }
    }

    /// <summary>A folder's files, or none where the folder is gone or will not be read.</summary>
    private IReadOnlyList<string> Listed(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(directory, pattern).ToList();
        }
        catch (Exception ex)
        {
            // A folder this plugin cannot read is a folder it never wrote to.
            _logger.LogWarning(ex, "DialogueBoost: could not list {Directory}", directory);
            return Array.Empty<string>();
        }
    }

    /// <summary>The files the state database still names, whatever the profiles are called now.</summary>
    private async Task FindByRecordAsync(HashSet<string> files, Dictionary<Guid, BaseItem> items)
    {
        foreach (var record in await _state.GetAllRecordsAsync().ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(record.SidecarPath) || !File.Exists(record.SidecarPath))
            {
                continue;
            }

            files.Add(record.SidecarPath);

            if (Guid.TryParse(record.ItemId, out var id) && _library.ById(id) is BaseItem item)
            {
                items[id] = item;
            }
        }
    }
}
