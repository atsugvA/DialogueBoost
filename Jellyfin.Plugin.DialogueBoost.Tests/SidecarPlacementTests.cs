using System.IO;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Output;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// Which folder a track goes in, and the places it could have been left — the half of a track's
/// path that <see cref="SidecarNamer"/> does not decide.
/// </summary>
public class SidecarPlacementTests
{
    private const string Source = "/media/movies/Film (2020)/Film (2020).mkv";

    private readonly FakeMediaLibrary _library = new("/var/lib/jellyfin/metadata");
    private readonly SidecarPlacement _placement;

    public SidecarPlacementTests()
    {
        _placement = new SidecarPlacement(_library);
    }

    /// <summary>
    /// The same file name in both places — that is how Jellyfin matches it to the video in either —
    /// and only the folder differs.
    /// </summary>
    [Fact]
    public void PathFor_IsTheSameNameInTheFolderTheSettingNames()
    {
        var item = _library.Add(Source);
        string metadata = _library.MetadataFolderOf(item);

        Assert.Equal(
            "/media/movies/Film (2020)/Film (2020).Dialogue Boost.default.mka",
            _placement.PathFor(item, "Dialogue Boost", claimsDefaultTrack: true, SidecarLocation.BesideMedia));
        Assert.Equal(
            Path.Combine(metadata, "Film (2020).Dialogue Boost.default.mka"),
            _placement.PathFor(item, "Dialogue Boost", claimsDefaultTrack: true, SidecarLocation.MetadataFolder));
    }

    /// <summary>
    /// Everything that deletes asks about both names in both folders: Jellyfin reads both folders
    /// whatever the setting says, so a track under either is in the audio menu.
    /// </summary>
    [Fact]
    public void CandidatePaths_AreBothNamesInBothFolders()
    {
        var item = _library.Add(Source);
        string metadata = _library.MetadataFolderOf(item);

        var paths = _placement.CandidatePaths(item, "Dialogue Boost").ToList();

        Assert.Equal(
            new[]
            {
                Path.Combine(metadata, "Film (2020).Dialogue Boost.mka"),
                Path.Combine(metadata, "Film (2020).Dialogue Boost.default.mka"),
                "/media/movies/Film (2020)/Film (2020).Dialogue Boost.mka",
                "/media/movies/Film (2020)/Film (2020).Dialogue Boost.default.mka"
            }.OrderBy(p => p),
            paths.OrderBy(p => p));
    }

    [Fact]
    public void CopiesElsewhere_IsTheSameNameInTheOtherFolderOnly()
    {
        var item = _library.Add(Source);
        string written = _placement.PathFor(item, "Dialogue Boost", claimsDefaultTrack: true, SidecarLocation.MetadataFolder);

        Assert.Equal(
            new[] { "/media/movies/Film (2020)/Film (2020).Dialogue Boost.default.mka" },
            _placement.CopiesElsewhere(item, written));
    }

    /// <summary>A virtual entry has no file, so no folder of either kind — never a throw.</summary>
    [Fact]
    public void AnItemWithNoFile_HasNoFolders()
    {
        var item = new Movie { Name = "Virtual" };

        Assert.Null(_placement.FolderFor(item, SidecarLocation.BesideMedia));
        Assert.Null(_placement.FolderFor(item, SidecarLocation.MetadataFolder));
        Assert.Empty(_placement.FoldersOf(item));
        Assert.Empty(_placement.CandidatePaths(item, "Dialogue Boost"));
    }
}
