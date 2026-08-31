using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.DialogueBoost.Output;

/// <summary>
/// Which folder an item's tracks go in, and every folder one of them could have been left in.
/// </summary>
/// <remarks>
/// <see cref="SidecarNamer"/> decides what a track is called; this decides where it lives. There are
/// two places (<see cref="SidecarLocation"/>) and the setting can change between runs, so everything
/// that finds or deletes tracks asks about both: Jellyfin reads both folders whatever the setting
/// says, so a track left under the other one is still a row in the audio menu.
/// </remarks>
public sealed class SidecarPlacement
{
    private static readonly SidecarLocation[] Everywhere = Enum.GetValues<SidecarLocation>();

    private readonly IMediaLibrary _library;

    public SidecarPlacement(IMediaLibrary library)
    {
        _library = library;
    }

    /// <summary>
    /// The folder an item's tracks go in at <paramref name="location"/>, or <c>null</c> for an item
    /// with no file of its own.
    /// </summary>
    public string? FolderFor(BaseItem item, SidecarLocation location)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        string? folder = location == SidecarLocation.BesideMedia
            ? Path.GetDirectoryName(item.Path)
            : _library.MetadataFolderOf(item);

        return string.IsNullOrEmpty(folder) ? null : folder;
    }

    /// <summary>Where this profile's track for this item belongs at <paramref name="location"/>.</summary>
    public string PathFor(BaseItem item, string marker, bool claimsDefaultTrack, SidecarLocation location)
    {
        string folder = FolderFor(item, location)
            ?? throw new ArgumentException($"'{item.Name}' has no file to write a track for.", nameof(item));

        return Path.Combine(folder, SidecarNamer.FileName(item.Path, marker, claimsDefaultTrack));
    }

    /// <summary>Every folder an item's tracks could be in, whatever the setting says now.</summary>
    public IEnumerable<string> FoldersOf(BaseItem item) =>
        Everywhere
            .Select(location => FolderFor(item, location))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Every path this profile could have written for this item: both names, in both folders.
    /// </summary>
    public IEnumerable<string> CandidatePaths(BaseItem item, string marker) =>
        FoldersOf(item).SelectMany(folder =>
            SidecarNamer.CandidateNames(item.Path, marker).Select(name => Path.Combine(folder, name)));

    /// <summary>
    /// The same track in the other folder: what a record forgotten across a change of
    /// <see cref="SidecarLocation"/> leaves behind once the track is written again.
    /// </summary>
    public IEnumerable<string> CopiesElsewhere(BaseItem item, string trackPath) =>
        FoldersOf(item)
            .Select(folder => Path.Combine(folder, Path.GetFileName(trackPath)))
            .Where(path => !path.Equals(trackPath, StringComparison.Ordinal));
}
