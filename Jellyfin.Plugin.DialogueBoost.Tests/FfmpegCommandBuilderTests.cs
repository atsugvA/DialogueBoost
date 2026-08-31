using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class FfmpegCommandBuilderTests
{
    private const string Source = "/media/movies/film.mkv";
    private const string Destination = "/media/movies/film.Dialogue Boost.mka";

    private static List<AudioStreamInfo> RussianAndEnglish() => new()
    {
        new AudioStreamInfo { Index = 0, AudioIndex = 0, Language = "rus", Channels = 6, ChannelLayout = "5.1", IsDefault = true },
        new AudioStreamInfo { Index = 1, AudioIndex = 1, Language = "eng", Channels = 2, ChannelLayout = "stereo", IsDefault = false }
    };

    private static List<AudioStreamInfo> RussianOnly() => new()
    {
        new AudioStreamInfo { Index = 0, AudioIndex = 0, Language = "rus", Channels = 6, ChannelLayout = "5.1", IsDefault = true }
    };

    /// <summary>Asserts that <paramref name="flag"/> appears immediately followed by <paramref name="value"/>.</summary>
    private static void AssertOption(IReadOnlyList<string> args, string flag, string value)
    {
        var found = Enumerable.Range(0, args.Count - 1)
            .Where(i => args[i] == flag)
            .Select(i => args[i + 1])
            .ToList();

        Assert.True(found.Count > 0, $"'{flag}' is not in the command: {string.Join(' ', args)}");
        Assert.Contains(value, found);
    }

    /// <summary>
    /// One track per language processed, and nothing else. Copying the originals in bought nothing
    /// but a duplicate row in the audio menu and 3-5 GB per movie per profile.
    /// </summary>
    [Fact]
    public void BuildCommand_ProcessesOneLanguage_WritesThatTrackAndNoCopies()
    {
        var profile = new DialogueBoostProfile
        {
            ProcessLanguages = new List<string> { "rus" },
            SetAsDefaultTrack = false,
            CenterChannelGainDb = 4.0
        };

        var spec = FfmpegCommandBuilder.BuildCommand(Source, RussianAndEnglish(), profile);
        var args = spec.ArgumentsWritingTo(Destination);

        Assert.Equal(1, spec.TotalOutputStreams);
        AssertOption(args, "-map", "0:a:0");
        Assert.DoesNotContain("0:a:1", args);
        Assert.DoesNotContain("copy", args);
        AssertOption(args, "-c:a:0", "eac3");
        AssertOption(args, "-f", "matroska");
    }

    /// <summary>The source ordinal is what <c>-map 0:a:N</c> means, not the output position.</summary>
    [Fact]
    public void BuildCommand_TwoLanguages_MapsEachSourceOrdinalToItsOwnOutputStream()
    {
        var profile = new DialogueBoostProfile { ProcessLanguages = new List<string>() };

        var spec = FfmpegCommandBuilder.BuildCommand(Source, RussianAndEnglish(), profile);
        var args = spec.ArgumentsWritingTo(Destination);

        Assert.Equal(2, spec.TotalOutputStreams);
        AssertOption(args, "-map", "0:a:0");
        AssertOption(args, "-map", "0:a:1");
        AssertOption(args, "-metadata:s:a:0", "language=rus");
        AssertOption(args, "-metadata:s:a:1", "language=eng");
    }

    [Fact]
    public void BuildCommand_SetAsDefaultTrackTrue_FlagsTheTrackInTheSourcesOwnLanguage()
    {
        var profile = new DialogueBoostProfile
        {
            ProcessLanguages = new List<string>(),
            SetAsDefaultTrack = true,
            CenterChannelGainDb = 4.0
        };

        var args = FfmpegCommandBuilder.BuildCommand(Source, RussianAndEnglish(), profile).ArgumentsWritingTo(Destination);

        // Russian is the source's own default; the English track is in the same sidecar and is not.
        AssertOption(args, "-disposition:a:0", "default");
        AssertOption(args, "-disposition:a:1", "0");
    }

    /// <summary>
    /// Nothing in the sidecar is flagged, so Jellyfin falls back to the embedded default — measured:
    /// a multi-stream external file with no flag leaves the embedded track default.
    /// </summary>
    [Fact]
    public void BuildCommand_SetAsDefaultTrackFalse_FlagsNothing()
    {
        var profile = new DialogueBoostProfile
        {
            ProcessLanguages = new List<string>(),
            SetAsDefaultTrack = false,
            CenterChannelGainDb = 4.0
        };

        var args = FfmpegCommandBuilder.BuildCommand(Source, RussianAndEnglish(), profile).ArgumentsWritingTo(Destination);

        AssertOption(args, "-disposition:a:0", "0");
        AssertOption(args, "-disposition:a:1", "0");
        Assert.DoesNotContain("default", args);
    }

    [Fact]
    public void BuildCommand_TitleAndFilterAreWholeArgumentsWithNoQuoting()
    {
        var profile = new DialogueBoostProfile
        {
            ProcessLanguages = new List<string> { "rus" },
            SidecarNamingMarker = "Dialogue Boost",
            CenterChannelGainDb = 4.0
        };

        var args = FfmpegCommandBuilder.BuildCommand(Source, RussianOnly(), profile).ArgumentsWritingTo(Destination);

        // A marker with a space is one argument, not two, and carries no quotes of its own —
        // ffmpeg receives the title verbatim.
        AssertOption(args, "-metadata:s:a:0", "title=Dialogue Boost");
        Assert.DoesNotContain(args, a => a.Contains('"'));
    }

    /// <summary>
    /// The destination is a parameter of the runnable list, not something spliced into a finished
    /// command string afterwards.
    /// </summary>
    [Fact]
    public void ArgumentsWritingTo_PutsThePathLastVerbatimAndChangesNothingElse()
    {
        var profile = new DialogueBoostProfile { ProcessLanguages = new List<string> { "rus" } };
        var spec = FfmpegCommandBuilder.BuildCommand(Source, RussianOnly(), profile);

        const string Temp = "/media/movies/.dialogueboost-tmp-9fc3.mka";
        var final = spec.ArgumentsWritingTo(Destination);
        var temp = spec.ArgumentsWritingTo(Temp);

        Assert.Equal(Destination, final[^1]);
        Assert.Equal(Temp, temp[^1]);
        Assert.Equal(final.Take(final.Count - 1), temp.Take(temp.Count - 1));
        Assert.Contains(Source, final);
    }

    /// <summary>
    /// The old builder quoted paths into one command string and the writer then found the output
    /// path by searching that string. A quote, a newline or the sidecar's own name inside the
    /// source path corrupted the command; a list has no such reading to do.
    /// </summary>
    [Theory]
    [InlineData("/media/a \"quoted\" film.mkv", "/media/a \"quoted\" film.Dialogue Boost.mka")]
    [InlineData("/media/film.Dialogue Boost.mka.mkv", "/media/film.Dialogue Boost.mka")]
    [InlineData("/media/line\nbreak.mkv", "/media/line\nbreak.Dialogue Boost.mka")]
    [InlineData("C:\\Media\\Movies\\film.mkv", "C:\\Media\\Movies\\film.Dialogue Boost.mka")]
    public void BuildCommand_HostilePaths_SurviveAsSingleArguments(string source, string destination)
    {
        var profile = new DialogueBoostProfile { ProcessLanguages = new List<string> { "rus" } };

        var args = FfmpegCommandBuilder.BuildCommand(source, RussianOnly(), profile).ArgumentsWritingTo(destination);

        AssertOption(args, "-i", source);
        Assert.Equal(destination, args[^1]);
        Assert.Single(args, a => a == source);
    }

    /* ── which languages this profile has anything to do with ──────────────────────────────── */

    /// <summary>
    /// The question that tells a skip from a failure. A profile set to German against an
    /// English-only file has nothing to write, and it used to be found out by BuildCommand
    /// throwing — which the caller recorded as <c>Failed</c>, so the page counted it as work
    /// outstanding and every run retried it.
    /// </summary>
    [Fact]
    public void TargetLanguages_NoneOfTheProfilesLanguagesArePresent_IsEmpty()
    {
        var profile = new DialogueBoostProfile { ProcessLanguages = new List<string> { "deu" } };

        Assert.Empty(FfmpegCommandBuilder.TargetLanguages(RussianAndEnglish(), profile));
    }

    [Fact]
    public void TargetLanguages_OnlyThoseTheProfileAsksFor()
    {
        var profile = new DialogueBoostProfile { ProcessLanguages = new List<string> { "eng", "deu" } };

        Assert.Equal(new[] { "eng" }, FfmpegCommandBuilder.TargetLanguages(RussianAndEnglish(), profile));
    }

    /// <summary>An unconfigured profile processes whatever the file has.</summary>
    [Fact]
    public void TargetLanguages_NoLanguagesConfigured_MeansEveryLanguage()
    {
        var profile = new DialogueBoostProfile { ProcessLanguages = new List<string>() };

        Assert.Equal(new[] { "rus", "eng" }, FfmpegCommandBuilder.TargetLanguages(RussianAndEnglish(), profile));
    }

    /// <summary>
    /// Night Mode, Speech and EBU R128 all run a filter that hands back the channels it was given,
    /// so the only open question is what will encode them — and asking aac for everything, which is
    /// what all three used to do, fails a height layout outright with
    /// <c>Unsupported channel layout "5.1.4"</c>. They take the same carrier the Dialogue Boost
    /// branch does.
    /// </summary>
    [Theory]
    [InlineData("5.1", 6, "eac3", "640k")]
    [InlineData("7.1", 8, "aac", "512k")]
    [InlineData("6.1", 7, "aac", "448k")]
    [InlineData("5.1.4", 10, "eac3", "640k")]
    [InlineData("stereo", 2, "aac", "256k")]
    public void BuildCommand_AProfileThatKeepsTheChannelCount_TakesACodecThatCanCarryIt(
        string layout, int channels, string codec, string rate)
    {
        var streams = new List<AudioStreamInfo>
        {
            new() { Index = 0, AudioIndex = 0, Language = "deu", Channels = channels, ChannelLayout = layout, IsDefault = true }
        };

        // Every language, explicitly: the question here is the carrier, not the filter.
        foreach (BaseProfileConfig profile in new BaseProfileConfig[]
                 {
                     new NightModeProfile { BitrateMode = Configuration.BitrateMode.Preset, ProcessLanguages = new List<string>() },
                     new SpeechProfile { BitrateMode = Configuration.BitrateMode.Preset, ProcessLanguages = new List<string>() },
                     new Ebur128Profile { BitrateMode = Configuration.BitrateMode.Preset, ProcessLanguages = new List<string>() }
                 })
        {
            var args = FfmpegCommandBuilder.BuildCommand(Source, streams, profile).ArgumentsWritingTo(Destination);

            AssertOption(args, "-c:a:0", codec);
            AssertOption(args, "-b:a:0", rate);
        }
    }

    /// <summary>
    /// And the channel count they report is the one that encoder writes, which is what decides
    /// whether the source's own rate is still comparable. A 5.1.4 source folds to six channels in
    /// eac3, so its rate does not carry over.
    /// </summary>
    [Fact]
    public void BuildCommand_AProfileThatKeepsTheChannelCount_DoesNotCarryARateAcrossAFold()
    {
        var streams = new List<AudioStreamInfo>
        {
            new() { Index = 0, AudioIndex = 0, Language = "deu", Channels = 10, ChannelLayout = "5.1.4",
                    Codec = "eac3", BitrateBps = 1024000, IsDefault = true }
        };

        var args = FfmpegCommandBuilder
            .BuildCommand(Source, streams, new NightModeProfile
            {
                BitrateMode = Configuration.BitrateMode.Auto,
                ProcessLanguages = new List<string>()
            })
            .ArgumentsWritingTo(Destination);

        AssertOption(args, "-b:a:0", "640k");
    }
}
