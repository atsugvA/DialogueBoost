using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Output;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class SidecarNamerTests
{
    [Fact]
    public void FileName_IsTheSourcesNameThenTheMarker()
    {
        string videoPath = "/var/media/movies/Inception (2010).mkv";
        string marker = "Dialogue Boost";

        string fileName = SidecarNamer.FileName(videoPath, marker);

        Assert.Equal("Inception (2010).Dialogue Boost.mka", fileName);
    }

    /// <summary>
    /// Jellyfin's own filename token, and the only mechanism that works at every stream count: the
    /// container's default flag is ignored outright on a single-stream external file.
    /// </summary>
    [Fact]
    public void FileName_ClaimingTheDefaultTrack_AddsJellyfinsOwnToken()
    {
        string claimed = SidecarNamer.FileName(
            "/var/media/movies/Inception (2010).mkv", "Dialogue Boost", claimsDefaultTrack: true);

        Assert.Equal("Inception (2010).Dialogue Boost.default.mka", claimed);
    }

    /// <summary>
    /// What the two deleting call sites need: the name a profile writes today and the one it wrote
    /// while it held the claim. A file under the other name is a track nothing would collect.
    /// </summary>
    [Fact]
    public void CandidateNames_AreBothNamesTheProfileCouldHaveWritten()
    {
        var names = SidecarNamer.CandidateNames("/media/film.mkv", "Dialogue Boost").ToList();

        Assert.Equal(new[] { "film.Dialogue Boost.mka", "film.Dialogue Boost.default.mka" }, names);
    }

    /// <summary>
    /// Beside its target, so publishing is a rename — but never under a name that starts with the
    /// video's, because that is how Jellyfin decides a file is one of the video's tracks.
    /// </summary>
    [Fact]
    public void TempPathBeside_IsInTheTargetsFolder_UnderANameJellyfinWillNotList()
    {
        const string Target = "/var/media/movies/Inception (2010).Dialogue Boost.default.mka";

        string temp = SidecarNamer.TempPathBeside(Target);

        Assert.Equal("/var/media/movies", System.IO.Path.GetDirectoryName(temp));
        string name = System.IO.Path.GetFileName(temp);
        Assert.StartsWith(".dialogueboost-tmp-", name, System.StringComparison.Ordinal);
        Assert.EndsWith(".mka", name, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Inception", name, System.StringComparison.Ordinal);
        Assert.NotEqual(temp, SidecarNamer.TempPathBeside(Target));
    }

    [Theory]
    [InlineData(".dialogueboost-tmp-0123456789abcdef0123456789abcdef.mka", true)]
    [InlineData("Inception (2010).Dialogue Boost.default.mka.tmp_0123456789abcdef0123456789abcdef.mka", true)]
    [InlineData("Inception (2010).Dialogue Boost.default.mka", false)]
    [InlineData("Inception (2010).Commentary.mka", false)]
    [InlineData("Inception (2010).mka.tmp_mine.mka", false)]
    [InlineData(".dialogueboost-write-probe-0123456789abcdef0123456789abcdef", false)]
    public void IsTemporary_KnowsBothNamingsAndNothingElse(string fileName, bool expected)
    {
        Assert.Equal(expected, SidecarNamer.IsTemporary(fileName));
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
