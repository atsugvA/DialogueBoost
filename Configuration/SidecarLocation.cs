namespace Jellyfin.Plugin.DialogueBoost.Configuration;

/// <summary>
/// Where the tracks this plugin writes are kept.
/// </summary>
/// <remarks>
/// Jellyfin reads an item's external audio from both places on every refresh, whatever this says,
/// and lists, plays and claims the default from either identically. What differs is who else
/// can see the file, and which disk pays for it.
/// </remarks>
public enum SidecarLocation
{
    /// <summary>
    /// Jellyfin's own folder for the item, under its metadata path. Nothing but Jellyfin looks
    /// there: a library manager such as Radarr or Sonarr — which adopts an unknown file named after
    /// a video as one of the video's extras, and renames, moves and deletes it along with the video —
    /// never sees the track, and neither does a torrent client seeding the media folder. Jellyfin
    /// deletes the folder when the item leaves the library. It costs space on the disk Jellyfin
    /// keeps its own data on, and needs no write access to the media at all.
    /// </summary>
    MetadataFolder,

    /// <summary>
    /// Beside the source file, in the media folder — where any other player can use the track too,
    /// and any other tool can adopt it.
    /// </summary>
    BesideMedia
}
