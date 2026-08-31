using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Integration;

/// <summary>
/// Making a sidecar visible. Jellyfin finds an external audio file when it re-reads the item, so
/// every published track needs one of these before anybody can select it.
/// </summary>
public interface IMetadataRefresher
{
    /// <summary>Re-reads one item and waits for it. For the profile that asks for it per item.</summary>
    Task RefreshNowAsync(BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads a batch ourselves, a few at a time, and waits for all of them.
    /// </summary>
    /// <remarks>
    /// It used to hand the batch to Jellyfin's own refresh queue, which is a
    /// <c>PriorityQueue</c> enqueued outside its own lock and drained under none. A tight loop of
    /// <c>QueueRefresh</c> calls races the drain it starts, and a value that <c>QueueRefresh</c>
    /// refuses to accept comes back out — <c>Guid can't be empty</c>, seen in this server's log
    ///. Nothing was lost the day it was measured, but the same interleaving can drop a real
    /// refresh, and a dropped refresh is a written track nobody can select.
    /// </remarks>
    Task RefreshBatchAsync(IReadOnlyCollection<BaseItem> items, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class JellyfinMetadataRefresher : IMetadataRefresher
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<JellyfinMetadataRefresher> _logger;

    public JellyfinMetadataRefresher(
        IFileSystem fileSystem,
        ILogger<JellyfinMetadataRefresher> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task RefreshNowAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Re-reading '{ItemName:l}' so its new track is selectable.", item.Name);
            await item.RefreshMetadata(Options(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The sidecar is written and recorded; only its visibility is delayed, and the next
            // scan or refresh will pick it up. Not worth failing the item over.
            _logger.LogError(ex, "Failed to refresh '{ItemName:l}' ({ItemId}).", item.Name, item.Id);
        }
    }

    public async Task RefreshBatchAsync(IReadOnlyCollection<BaseItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Re-reading {Count} item(s) so their new tracks are selectable.", items.Count);

        // Bounded, and deliberately small: a refresh reads the media file and writes the library
        // database, and Jellyfin's own queue drains one at a time. Four is more than that and far
        // less than the encode loop that just finished, which is the point — the encodes are done.
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(
                items,
                options,
                async (item, ct) => await RefreshNowAsync(item, ct).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Metadata only, and forced. Images are not touched — nothing about a new audio track changes
    /// a poster — and <c>ForceSave</c> is what makes the re-read of the media file actually happen
    /// rather than being skipped as unchanged. Verified live: an item whose sidecar had just been
    /// written showed it as an external track about a second after this refresh.
    /// </summary>
    private MetadataRefreshOptions Options() =>
        new(new DirectoryService(_fileSystem))
        {
            ImageRefreshMode = MetadataRefreshMode.None,
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false,
            ForceSave = true
        };
}
