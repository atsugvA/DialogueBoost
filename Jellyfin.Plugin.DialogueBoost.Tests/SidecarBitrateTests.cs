using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// The library's common case, measured 2026-08-28: 309 streams of <c>ac3 5.1 @ 384k</c>, every one
/// of them re-encoded at 640k because the rate was a constant per branch.
/// </summary>
public class SidecarBitrateTests
{
    private static AudioStreamInfo Source(int channels, string layout, string codec, int kbps) => new()
    {
        AudioIndex = 0,
        Language = "deu",
        Codec = codec,
        Channels = channels,
        ChannelLayout = layout,
        BitrateBps = kbps * 1000
    };

    private static string Resolve(BaseProfileConfig profile, AudioStreamInfo source) =>
        SidecarBitrate.Resolve(profile, source, ChannelLayoutDetector.BuildDialogueBoostBranchSpec(source, 4.0));

    [Fact]
    public void Auto_ChannelCountUnchanged_TakesTheSourcesOwnRate()
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Auto };

        Assert.Equal("384k", Resolve(profile, Source(6, "5.1", "ac3", 384)));
    }

    /// <summary>
    /// <c>dialoguenhance</c> takes stereo and produces 3.0 (measured), and the downmix branch
    /// produces stereo from 5.1. A rate quoted for the source says nothing about either.
    /// </summary>
    [Theory]
    [InlineData(2, "stereo", 192)]
    [InlineData(6, "5.1(unnamed)", 384)]
    public void Auto_ChannelCountChanged_FallsBackToTheProfileDefault(int channels, string layout, int kbps)
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Auto };

        Assert.Equal("256k", Resolve(profile, Source(channels, layout, "ac3", kbps)));
    }

    [Theory]
    [InlineData("truehd")]
    [InlineData("flac")]
    [InlineData("dts")]
    [InlineData("wavpack")]
    [InlineData("wmalossless")]
    // Uncompressed, where the rate is arithmetic on the sample format: 6 x 48000 x 16 is 4608 kbps
    // whatever the audio is. Found by running a PCM 5.1 fixture end to end — the sidecar came out
    // at 4608k and 12 MB against 1.6 MB for every other one on that rig.
    [InlineData("pcm_s16le")]
    [InlineData("pcm_s24le")]
    [InlineData("pcm_bluray")]
    [InlineData("dsd_lsbf")]
    public void Auto_ALosslessSourceHasNoRateWorthMatching(string codec)
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Auto };

        Assert.Equal("640k", Resolve(profile, Source(6, "5.1", codec, 4608)));
    }

    /// <summary>A lossy codec whose name merely starts the same way is still followed.</summary>
    [Fact]
    public void Auto_ALossyCodecIsNotCaughtByTheLosslessPrefixes()
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Auto };

        Assert.Equal("448k", Resolve(profile, Source(6, "5.1", "eac3", 448)));
        Assert.Equal("384k", Resolve(profile, Source(6, "5.1", "vorbis", 384)));
    }

    /// <summary>The ffprobe fallback path finds no rate on a container that does not store one.</summary>
    [Fact]
    public void Auto_NothingReportedARate_FallsBackToTheProfileDefault()
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Auto };

        Assert.Equal("640k", Resolve(profile, Source(6, "5.1", "ac3", 0)));
    }

    [Fact]
    public void Preset_IsTodaysConstant_WhateverTheSourceIs()
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Preset };

        Assert.Equal("640k", Resolve(profile, Source(6, "5.1", "ac3", 384)));
        Assert.Equal("256k", Resolve(profile, Source(2, "stereo", "ac3", 192)));
    }

    [Fact]
    public void Manual_IsTheRateTheUserNamed()
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Manual, BitrateKbps = 448 };

        Assert.Equal("448k", Resolve(profile, Source(6, "5.1", "ac3", 384)));
        Assert.Equal("448k", Resolve(profile, Source(2, "stereo", "ac3", 192)));
    }

    /// <summary>
    /// Measured on jellyfin-ffmpeg 7.1.4: eac3 fails the frame at 32 kbps and refuses to open above
    /// 5644 kbps. A source tagged below the floor is written at the floor rather than not at all.
    /// </summary>
    [Fact]
    public void Auto_ARateTheEncoderWouldRefuse_IsBroughtInsideWhatItTakes()
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Auto };

        Assert.Equal("40k", Resolve(profile, Source(6, "5.1", "ac3", 8)));
        Assert.Equal("5644k", Resolve(profile, Source(6, "5.1", "ac3", 9000)));
    }

    /// <summary>The rate reaches the command, not just the resolver.</summary>
    [Fact]
    public void BuildCommand_AutoOverA384kSource_AsksFor384k()
    {
        var profile = new DialogueBoostProfile { BitrateMode = BitrateMode.Auto, ProcessLanguages = new List<string>() };
        var streams = new List<AudioStreamInfo> { Source(6, "5.1", "ac3", 384) };

        var args = FfmpegCommandBuilder.BuildCommand("/media/film.mkv", streams, profile)
            .ArgumentsWritingTo("/media/film.Dialogue Boost.mka")
            .ToList();

        int at = args.IndexOf("-b:a:0");
        Assert.True(at >= 0, "the command names no bitrate");
        Assert.Equal("384k", args[at + 1]);
    }
}
