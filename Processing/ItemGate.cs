using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Output;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// Whether an item is worth looking at, and what to say if it is not.
/// </summary>
/// <param name="Proceed">False when nothing further should happen to this item.</param>
/// <param name="Reason">Why not, in the words the record and the log use. Empty when proceeding.</param>
/// <param name="IsWatched">
/// Whether the accounts that count have played it. Carried out of the gate because it decides more
/// than entry: a sidecar written for something already watched is marked exempt from the cleanup
/// that would immediately delete it again.
/// </param>
public sealed record GateDecision(bool Proceed, string Reason, bool IsWatched)
{
    public static GateDecision Go(bool isWatched) => new(true, string.Empty, isWatched);

    public static GateDecision Stop(string reason, bool isWatched = false) => new(false, reason, isWatched);
}

/// <summary>
/// The conditions that decide whether an item is touched at all — before any profile, any probe and
/// any encode.
/// </summary>
/// <remarks>
/// These used to be the first hundred lines of <c>ProcessItemAsync</c>, interleaved with the work
/// itself. They are one question — may we, and should we — and they are answered once per item
/// rather than once per profile.
/// </remarks>
public sealed class ItemGate
{
    /// <summary>
    /// What a run leaves free on the disk holding Jellyfin's metadata folder. Jellyfin refuses to
    /// start with less than 2 GiB free on its own paths — it says so at every start, "successfully
    /// checked with … free which is over the minimum of 2GiB" — and it shares that disk with its
    /// database, its logs and its transcodes. A library's worth of tracks must not be what takes it
    /// there, so the plugin stops well above it.
    /// </summary>
    internal const long JellyfinDiskReserveBytes = 5L * 1024 * 1024 * 1024;

    private readonly WatchedItems _watched;
    private readonly StorageProbe _storage;
    private readonly SidecarPlacement _placement;
    private readonly IPlaybackSessions _sessions;
    private readonly ILogger<ItemGate> _logger;

    public ItemGate(
        WatchedItems watched,
        StorageProbe storage,
        SidecarPlacement placement,
        IPlaybackSessions sessions,
        ILogger<ItemGate> logger)
    {
        _watched = watched;
        _storage = storage;
        _placement = placement;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<GateDecision> CheckAsync(
        BaseItem item,
        PluginConfiguration config,
        bool overrideWatched,
        CancellationToken cancellationToken)
    {
        if (!config.Enabled)
        {
            _logger.LogInformation("DialogueBoost is switched off; '{ItemName:l}' is left alone.", item.Name);
            return GateDecision.Stop("plugin disabled");
        }

        bool isWatched = _watched.IsWatched(item, config);
        if (config.SkipWatchedItems && !overrideWatched && isWatched)
        {
            _logger.LogInformation("'{ItemName:l}' has been watched by everyone who counts; skipping.", item.Name);
            return GateDecision.Stop("already watched", isWatched);
        }

        var reachable = CheckReachable(item);
        if (!reachable.Proceed)
        {
            return reachable with { IsWatched = isWatched };
        }

        await WaitForPlaybackToStopAsync(config, cancellationToken).ConfigureAwait(false);

        var room = CheckRoom(item, config.SidecarLocation);
        return room.Proceed ? GateDecision.Go(isWatched) : room with { IsWatched = isWatched };
    }

    /// <summary>
    /// Whether the source file is there — and, when it is not, which kind of "not".
    /// </summary>
    /// <remarks>
    /// "The file is gone" and "we are not allowed to look into the folder" are the same answer from
    /// <see cref="File.Exists"/>, and only one of them is about the file. A folder the service user
    /// cannot enter is the reported failure mode of this media volume, so it is worth telling the
    /// two apart before blaming the path.
    /// </remarks>
    private GateDecision CheckReachable(BaseItem item)
    {
        string sourcePath = item.Path;
        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            return GateDecision.Go(false);
        }

        var access = _storage.Check(Path.GetDirectoryName(sourcePath));
        if (!string.IsNullOrWhiteSpace(sourcePath) && !access.IsWritable)
        {
            _logger.LogError("DialogueBoost: cannot reach '{Path:l}'. {Explanation:l}", sourcePath, access.Explain());
            return GateDecision.Stop(access.Explain());
        }

        _logger.LogWarning("'{ItemName:l}' has no file at '{Path:l}'.", item.Name, sourcePath);
        return GateDecision.Stop("source file missing");
    }

    /// <summary>
    /// Holds work back while somebody is watching, if the setting asks for it.
    /// </summary>
    /// <remarks>
    /// A poll, and knowingly: Jellyfin raises playback events, but what is wanted here is not an
    /// event — it is "resume when the last one stops", and asking every five seconds is both the
    /// simplest and the cheapest way to know that.
    /// </remarks>
    private async Task WaitForPlaybackToStopAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!config.PauseDuringActivePlayback)
        {
            return;
        }

        bool announced = false;
        while (_sessions.AnyPlaying)
        {
            if (!announced)
            {
                _logger.LogInformation("Somebody is watching; holding off until they stop.");
                announced = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the disk the track goes to has room. A sidecar is well under a tenth of the source,
    /// so a tenth is a margin rather than an estimate; the floor stops the check waving through a
    /// nearly full disk because the source happened to be small. On the disk holding Jellyfin's
    /// metadata folder, <see cref="JellyfinDiskReserveBytes"/> comes on top.
    /// </summary>
    /// <remarks>
    /// Measured on the folder being written, through <see cref="StorageProbe.FreeBytesAt"/> — which
    /// is not the source's folder when tracks go to the metadata folder. It used to ask
    /// <c>DriveInfo</c> about the folder's <em>root</em>, which on Linux is <c>/</c> for every path,
    /// so a media volume at <c>/mnt/…</c> was judged by the system disk's free space.
    /// </remarks>
    private GateDecision CheckRoom(BaseItem item, SidecarLocation location)
    {
        try
        {
            string? directory = _placement.FolderFor(item, location);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return GateDecision.Go(false);
            }

            if (StorageProbe.FreeBytesAt(directory) is not long available)
            {
                _logger.LogWarning("Could not read free space for '{ItemName:l}'. Proceeding anyway.", item.Name);
                return GateDecision.Go(false);
            }

            bool jellyfinsDisk = location == SidecarLocation.MetadataFolder;
            long required = Math.Max(new FileInfo(item.Path).Length / 10, 100L * 1024 * 1024)
                + (jellyfinsDisk ? JellyfinDiskReserveBytes : 0);
            if (available >= required)
            {
                return GateDecision.Go(false);
            }

            if (jellyfinsDisk)
            {
                _logger.LogWarning(
                    "Not enough room for '{ItemName:l}' in Jellyfin's metadata folder: {AvailableMB} MB free, {RequiredMB} MB wanted. " +
                    "Jellyfin will not start with less than 2 GiB free on its own disk, so the plugin keeps 5 GiB of it clear. " +
                    "Free some space there, or write tracks beside the media instead.",
                    item.Name, available / (1024 * 1024), required / (1024 * 1024));
                return GateDecision.Stop("not enough free space in Jellyfin's metadata folder");
            }

            _logger.LogWarning(
                "Not enough room for '{ItemName:l}': {AvailableMB} MB free, {RequiredMB} MB wanted.",
                item.Name, available / (1024 * 1024), required / (1024 * 1024));
            return GateDecision.Stop("not enough free space");
        }
        catch (Exception ex)
        {
            // A drive that will not report its free space is not a reason to refuse to write to it.
            _logger.LogWarning(ex, "Could not read free space for '{ItemName:l}'. Proceeding anyway.", item.Name);
            return GateDecision.Go(false);
        }
    }
}
