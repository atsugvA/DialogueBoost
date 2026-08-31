using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Output;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class SidecarNamerTests
{
    [Fact]
    public void GetSidecarPath_GeneratesCorrectFilename()
    {
        string videoPath = "/var/media/movies/Inception (2010).mkv";
        string marker = "Dialogue Boost";

        string sidecarPath = SidecarNamer.GetSidecarPath(videoPath, marker);

        Assert.Equal("/var/media/movies/Inception (2010).Dialogue Boost.mka", sidecarPath);
    }

    /// <summary>
    /// Jellyfin's own filename token, and the only mechanism that works at every stream count: the
    /// container's default flag is ignored outright on a single-stream external file.
    /// </summary>
    [Fact]
    public void GetSidecarPath_ClaimingTheDefaultTrack_AddsJellyfinsOwnToken()
    {
        string claimed = SidecarNamer.GetSidecarPath(
            "/var/media/movies/Inception (2010).mkv", "Dialogue Boost", claimsDefaultTrack: true);

        Assert.Equal("/var/media/movies/Inception (2010).Dialogue Boost.default.mka", claimed);
    }

    /// <summary>
    /// What the two deleting call sites need: the name a profile writes today and the one it wrote
    /// while it held the claim. A file under the other name is a track nothing would collect.
    /// </summary>
    [Fact]
    public void CandidatePaths_AreBothNamesTheProfileCouldHaveWritten()
    {
        var paths = SidecarNamer.CandidatePaths("/media/film.mkv", "Dialogue Boost").ToList();

        Assert.Equal(
            new[] { "/media/film.Dialogue Boost.mka", "/media/film.Dialogue Boost.default.mka" },
            paths);
    }

    [Theory]
    [InlineData("cd", false)]
    [InlineData("dvd", false)]
    [InlineData("default", false)]
    [InlineData("rus", false)]
    [InlineData("Dialogue Boost", true)]
    [InlineData("Broadband Night Mode", true)]
    public void IsMarkerValid_ValidatesReservedKeywords(string marker, bool expectedValid)
    {
        bool isValid = SidecarNamer.IsMarkerValid(marker, out _);
        Assert.Equal(expectedValid, isValid);
    }
}
