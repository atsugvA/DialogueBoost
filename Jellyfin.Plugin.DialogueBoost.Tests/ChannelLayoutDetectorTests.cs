using System;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class ChannelLayoutDetectorTests
{
    [Theory]
    [InlineData("5.1", 6, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("5.1(side)", 6, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("7.1", 8, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("6.1", 7, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("6.1(back)", 7, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("7.1(wide-side)", 8, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("5.1.4", 10, DialogueBoostBranch.CenterGainInPlace)]
    // A front centre is a front centre below 5.1 too: these used to be downmixed to stereo,
    // which threw away channels a codec was willing to carry.
    [InlineData("3.0", 3, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("4.0", 4, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("3.1", 4, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("5.0", 5, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("5.0(side)", 5, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("hexagonal", 6, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("7.0", 7, DialogueBoostBranch.CenterGainInPlace)]
    // Real ffmpeg layouts with no front centre: nothing to lift in place, so the voice is
    // separated out of the stereo image instead.
    [InlineData("6.1(front)", 7, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("quad", 4, DialogueBoostBranch.StereoDialogueEnhance)]
    // A layout string we cannot place is a disagreement, and is not guessed at: `pan` reads a
    // missing input channel as silence rather than refusing, so a wrong name empties channels.
    [InlineData("unknown", 6, DialogueBoostBranch.StereoDialogueEnhance)]
    // No layout string at all is a different thing: there is nothing to disagree with, and ffmpeg
    // will give the stream its default for that count — 5.1 at six channels, 6.1 at seven.
    [InlineData("", 6, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("", 7, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("", 8, DialogueBoostBranch.CenterGainInPlace)]
    // Four is the one count where the default (4.0) and the layout just as likely to be meant
    // (quad) disagree about what sits at index 2, and three defaults to 2.1, which has no centre.
    [InlineData("", 4, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("", 3, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("", 9, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("stereo", 2, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("mono", 1, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("", 1, DialogueBoostBranch.StereoDialogueEnhance)]
    // The name has to describe *this* stream. A layout of six channels in front of an eight-channel
    // track is not this track's layout, whichever of them is wrong, and the last two would be
    // dropped in silence.
    [InlineData("5.1", 8, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("7.1", 6, DialogueBoostBranch.StereoDialogueEnhance)]
    public void DetectBranch_ReturnsExpectedBranch(string layout, int channels, DialogueBoostBranch expected)
    {
        var stream = new AudioStreamInfo
        {
            ChannelLayout = layout,
            Channels = channels
        };

        var detected = ChannelLayoutDetector.DetectBranch(stream);
        Assert.Equal(expected, detected);
    }

    [Fact]
    public void BuildDialogueBoostBranchSpec_CalculatesLinearGainCorrectly()
    {
        var stream = new AudioStreamInfo { ChannelLayout = "5.1", Channels = 6 };
        // 4.0 dB gain -> 10^(4/20) = 1.584893...
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(stream, 4.0);

        Assert.Equal(DialogueBoostBranch.CenterGainInPlace, spec.Branch);
        Assert.Equal("eac3", spec.Codec);
        Assert.Equal("640k", spec.Bitrate);
        // c2 is where every layout with a front centre keeps it.
        Assert.Contains("c2=1.584893*c2", spec.FilterGraph);
        Assert.Contains("alimiter=limit=0.891", spec.FilterGraph);
    }

    /// <summary>
    /// One graph for everything with no centre to lift, and no hand-written fold in it. The graph
    /// this replaced named the channels of the layout it was folding — the silent-surround hazard,
    /// and the last
    /// place in the plugin that still carried it: on a 6.0(front) source, which holds
    /// FL FR FLC FRC SL SR, it asked for BL, BR, FC and LFE and wrote 0.9*FL and 0.9*FR alone.
    /// <c>dialoguenhance</c> asks for stereo instead and lets libswresample fold from the layout
    /// the decoder reports.
    /// </summary>
    [Theory]
    [InlineData("stereo", 2)]
    [InlineData("mono", 1)]
    [InlineData("quad", 4)]
    [InlineData("6.0(front)", 6)]
    [InlineData("unknown", 6)]
    public void BuildDialogueBoostBranchSpec_EnhanceBranch_IsOneGraphWithNoChannelNames(
        string layout, int channels)
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = layout, Channels = channels }, 4.0);

        Assert.Equal(DialogueBoostBranch.StereoDialogueEnhance, spec.Branch);
        Assert.Equal("aac", spec.Codec);
        Assert.Equal("256k", spec.Bitrate);
        Assert.Equal(3, spec.OutputChannels);
        Assert.Equal(
            "dialoguenhance=original=1:enhance=1:voice=2,alimiter=limit=0.891",
            spec.FilterGraph);
        Assert.DoesNotContain("pan=", spec.FilterGraph, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression this replaced: the graph named the layout's channels, and the layout was
    /// whatever an analyzer had called the stream. Jellyfin calls every 6-channel AC-3 track in the
    /// test library "5.1" and ffmpeg decodes all of them as 5.1(side), so <c>BL=BL|BR=BR</c> asked
    /// for two channels the decoder never supplied — and <c>pan</c> answers that with silence and
    /// no warning. Positions cannot miss: 5.1 and 5.1(side) order their channels identically, so
    /// c4 and c5 are the surrounds under either name.
    /// </summary>
    [Fact]
    public void BuildDialogueBoostBranchSpec_CentreBranch_NamesInputChannelsByPosition()
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = "5.1", Channels = 6 }, 4.0);

        Assert.Equal(
            "pan=5.1|c0=c0|c1=c1|c2=1.584893*c2|c3=c3|c4=c4|c5=c5,alimiter=limit=0.891",
            spec.FilterGraph);
        Assert.DoesNotContain("BL", spec.FilterGraph);
        Assert.DoesNotContain("SL", spec.FilterGraph);
    }

    /// <summary>
    /// Every layout the centre branch accepts carries each of its channels through exactly once,
    /// by position, with the centre the only one scaled — and the centre sits where that layout's
    /// own channel order puts it. The graphs were run through jellyfin-ffmpeg 7.1.4 and all eleven
    /// were accepted.
    /// </summary>
    [Theory]
    [InlineData("5.1", 6, 2)]
    [InlineData("5.1(side)", 6, 2)]
    [InlineData("6.1", 7, 2)]
    [InlineData("6.1(back)", 7, 2)]
    [InlineData("7.1", 8, 2)]
    [InlineData("7.1(wide)", 8, 2)]
    [InlineData("7.1(wide-side)", 8, 2)]
    [InlineData("5.1.2", 8, 2)]
    [InlineData("5.1.4", 10, 2)]
    [InlineData("7.1.2", 10, 2)]
    [InlineData("7.1.4", 12, 2)]
    public void BuildDialogueBoostBranchSpec_CentreBranch_CarriesEveryChannelOnceAndScalesTheCentre(
        string layout, int channels, int centreIndex)
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = layout, Channels = channels }, 4.0);

        string expected = string.Join('|', Enumerable.Range(0, channels)
            .Select(i => i == centreIndex ? $"c{i}=1.584893*c{i}" : $"c{i}=c{i}"));

        Assert.Equal(DialogueBoostBranch.CenterGainInPlace, spec.Branch);
        Assert.Equal($"pan={layout}|{expected},alimiter=limit=0.891", spec.FilterGraph);
    }

    /// <summary>
    /// What the encoder writes, not what the filter produces — and the codec is picked so that the
    /// two agree wherever they can. jellyfin-ffmpeg's eac3 encoder lists no layout above 5.1 and
    /// folds anything larger down to it without failing, so the layouts above its ceiling go to aac,
    /// which carries 6.1 as seven channels and 7.1 as eight. The rows still on eac3 above 5.1 are
    /// the height layouts aac refuses outright, where a fold is the only answer that cannot fail.
    /// </summary>
    [Theory]
    [InlineData("3.0", 3, "eac3", 3)]
    [InlineData("4.0", 4, "eac3", 4)]
    [InlineData("3.1", 4, "eac3", 4)]
    [InlineData("5.0", 5, "eac3", 5)]
    [InlineData("5.0(side)", 5, "eac3", 5)]
    [InlineData("4.1", 5, "eac3", 5)]
    [InlineData("5.1", 6, "eac3", 6)]
    [InlineData("5.1(side)", 6, "eac3", 6)]
    [InlineData("6.0", 6, "aac", 6)]
    [InlineData("hexagonal", 6, "aac", 6)]
    [InlineData("6.1", 7, "aac", 7)]
    [InlineData("6.1(back)", 7, "aac", 7)]
    [InlineData("7.0", 7, "aac", 7)]
    [InlineData("7.1", 8, "aac", 8)]
    [InlineData("7.1(wide)", 8, "aac", 8)]
    [InlineData("7.1(wide-side)", 8, "aac", 8)]
    [InlineData("octagonal", 8, "aac", 8)]
    [InlineData("3.1.2", 6, "eac3", 4)]
    [InlineData("5.1.2", 8, "eac3", 6)]
    [InlineData("5.1.4", 10, "eac3", 6)]
    [InlineData("7.1.4", 12, "eac3", 6)]
    [InlineData("22.2", 24, "eac3", 6)]
    public void BuildDialogueBoostBranchSpec_CentreBranch_ReportsTheChannelsTheEncoderWrites(
        string layout, int channels, string codec, int expected)
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = layout, Channels = channels }, 4.0);

        Assert.Equal(codec, spec.Codec);
        Assert.Equal(expected, spec.OutputChannels);
    }

    /// <summary>
    /// The rate a layout falls back to when the source's own cannot be followed: eac3 keeps the
    /// 640k it has always used, and aac gets 64 kbps a channel of what it will actually write.
    /// </summary>
    [Theory]
    [InlineData("5.1", 6, "640k")]
    [InlineData("5.1.4", 10, "640k")]
    [InlineData("6.1", 7, "448k")]
    [InlineData("7.1", 8, "512k")]
    [InlineData("6.0", 6, "384k")]
    public void BuildDialogueBoostBranchSpec_CentreBranch_PresetRateFollowsTheEncoder(
        string layout, int channels, string expected)
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = layout, Channels = channels }, 4.0);

        Assert.Equal(expected, spec.Bitrate);
    }

    /// <summary>
    /// A track that names no layout is encoded as the layout ffmpeg will decode it into — measured
    /// live, where a six-channel PCM track and a seven-channel AAC one both reach Jellyfin with no
    /// <c>ChannelLayout</c> at all. Before this they were downmixed to three channels for want of a
    /// name that was never going to arrive.
    /// </summary>
    [Theory]
    [InlineData(6, "pan=5.1|", "eac3", 6)]
    [InlineData(7, "pan=6.1|", "aac", 7)]
    [InlineData(8, "pan=7.1|", "aac", 8)]
    [InlineData(5, "pan=5.0|", "eac3", 5)]
    [InlineData(10, "pan=5.1.4|", "eac3", 6)]
    public void BuildDialogueBoostBranchSpec_UnnamedLayout_UsesFfmpegsDefaultForTheCount(
        int channels, string expectedGraphStart, string codec, int outputChannels)
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = string.Empty, Channels = channels }, 4.0);

        Assert.Equal(DialogueBoostBranch.CenterGainInPlace, spec.Branch);
        Assert.StartsWith(expectedGraphStart, spec.FilterGraph, StringComparison.Ordinal);
        Assert.Equal(codec, spec.Codec);
        Assert.Equal(outputChannels, spec.OutputChannels);
    }

    /// <summary>
    /// The layout the graph is handed is the table's own spelling, not the stream's. The lookup is
    /// case-insensitive because an analyzer's spelling is not ours to police; <c>pan</c> is not, and
    /// would refuse the layout outright.
    /// </summary>
    [Fact]
    public void BuildDialogueBoostBranchSpec_CentreBranch_UsesTheCanonicalLayoutSpelling()
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = " 5.1(SIDE) ", Channels = 6 }, 4.0);

        Assert.StartsWith("pan=5.1(side)|", spec.FilterGraph, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>pan</c> whose coefficients are all 1 is not a matrix — ffmpeg reads it as a channel
    /// remap, and a remap goes by name. Measured on a 7.1(wide) source reported as 7.1:
    /// <c>c0=c0|…|c7=c7</c> came back with FLC and FRC at -inf and everything else 6 dB down, while
    /// the same graph with one non-unity coefficient carried all eight channels through. At unity
    /// there is nothing to lift, so there is no reason to name a layout at all.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.0000001)]
    public void BuildDialogueBoostBranchSpec_NoGainToApply_BuildsNoPan(double gainDb)
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = "7.1", Channels = 8 }, gainDb);

        Assert.Equal(DialogueBoostBranch.CenterGainInPlace, spec.Branch);
        Assert.Equal("alimiter=limit=0.891", spec.FilterGraph);
        Assert.Equal("aac", spec.Codec);
        Assert.Equal(8, spec.OutputChannels);
    }

    [Fact]
    public void BuildDialogueBoostBranchSpec_TheSmallestGainThatSurvivesRounding_StillBuildsAPan()
    {
        var spec = ChannelLayoutDetector.BuildDialogueBoostBranchSpec(
            new AudioStreamInfo { ChannelLayout = "7.1", Channels = 8 }, 0.5);

        Assert.StartsWith("pan=7.1|", spec.FilterGraph, StringComparison.Ordinal);
    }

    /// <summary>
    /// Jellyfin reports a layout with its variant flattened off, measured one fixture per row. Five
    /// of those names cover layouts that disagree about the front centre or about the channels
    /// around it; the 5.1, 5.0 and quad families cover only their own (side) twin, which holds the
    /// same channels in the same order.
    /// </summary>
    [Theory]
    [InlineData("3.0", true)]
    [InlineData("6.0", true)]
    [InlineData("6.1", true)]
    [InlineData("7.0", true)]
    [InlineData("7.1", true)]
    [InlineData("5.1", false)]
    [InlineData("5.0", false)]
    [InlineData("quad", false)]
    [InlineData("stereo", false)]
    [InlineData("hexagonal", false)]
    [InlineData("7.1.4", false)]
    [InlineData("", false)]
    public void NeedsDecodedLayout_IsTrueOnlyWhereTheNameHidesADifferentShape(string layout, bool expected)
    {
        Assert.Equal(expected, ChannelLayoutDetector.NeedsDecodedLayout(layout));
    }

    /// <summary>
    /// And once the decoder has answered, the layout it named is the one the graph is built on —
    /// 6.1(front) has no front centre, so what was going to be a 4 dB lift of its LFE becomes the
    /// stereo enhancer instead.
    /// </summary>
    [Theory]
    [InlineData("6.1", 7, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("6.1(back)", 7, DialogueBoostBranch.CenterGainInPlace)]
    [InlineData("6.1(front)", 7, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("3.0(back)", 3, DialogueBoostBranch.StereoDialogueEnhance)]
    [InlineData("6.0(front)", 6, DialogueBoostBranch.StereoDialogueEnhance)]
    public void DetectBranch_ADecodedVariant_IsJudgedOnItsOwnShape(
        string layout, int channels, DialogueBoostBranch expected)
    {
        Assert.Equal(
            expected,
            ChannelLayoutDetector.DetectBranch(new AudioStreamInfo { ChannelLayout = layout, Channels = channels }));
    }
}
