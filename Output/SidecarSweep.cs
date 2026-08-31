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
/// <b>Beside every item in every library.</b> A sidecar's name is a pure function of the source
/// path and the profile's marker (<see cref="SidecarNamer"/>), so for every video item the library
/// holds there are exactly two names per profile — claimed and unclaimed. Read by listing each
/// directory once rather than testing each name: a release folder holds a whole season, so this is
/// one <c>readdir</c> where it would otherwise be ten <c>stat</c>s an episode.
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
    private readonly ProcessingStateRepository _state;
    private readonly ILogger<SidecarSweep> _logger;

    public SidecarSweep(
        IMediaLibrary library,
        ProcessingStateRepository state,
        ILogger<SidecarSweep> logger)
    {
        _library = library;
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

        FindBesideEveryItem(markers, files, items, cancellationToken);
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
    /// The names this plugin would write beside every item the library holds, listed a directory at
    /// a time.
    /// </summary>
    private void FindBesideEveryItem(
        IReadOnlyList<string> markers,
        HashSet<string> files,
        Dictionary<Guid, BaseItem> items,
        CancellationToken cancellationToken)
    {
        // directory → the sidecar names it could hold → the item each belongs to.
        var expected = new Dictionary<string, Dictionary<string, BaseItem>>(StringComparer.Ordinal);

        foreach (var library in _library.Libraries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in _library.Within(library, ProcessableItem.Kinds))
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(item.Path) ?? string.Empty;
                if (directory.Length == 0)
                {
                    continue;
                }

                if (!expected.TryGetValue(directory, out var names))
                {
                    names = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
                    expected[directory] = names;
                }

                foreach (var marker in markers)
                {
                    foreach (var candidate in SidecarNamer.CandidatePaths(item.Path, marker))
                    {
                        names[Path.GetFileName(candidate)] = item;
                    }
                }
            }
        }

        foreach (var (directory, names) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> present;
            try
            {
                present = Directory.EnumerateFiles(directory, "*.mka").ToList();
            }
            catch (Exception ex)
            {
                // A folder this plugin cannot read is a folder it never wrote to.
                _logger.LogWarning(ex, "DialogueBoost: could not list {Directory}", directory);
                continue;
            }

            foreach (var path in present)
            {
                if (names.TryGetValue(Path.GetFileName(path), out var item))
                {
                    files.Add(path);
                    items[item.Id] = item;
                }
            }
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
