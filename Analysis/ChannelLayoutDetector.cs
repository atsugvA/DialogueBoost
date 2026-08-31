using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.DialogueBoost.Analysis;

public enum DialogueBoostBranch
{
    /// <summary>Lift the front centre where it sits, and carry every other channel through.</summary>
    CenterGainInPlace,

    /// <summary>
    /// No front centre to lift, so separate the voice out of the stereo image instead.
    /// </summary>
    StereoDialogueEnhance
}

public class BranchExecutionSpec
{
    public DialogueBoostBranch Branch { get; set; }

    public string FilterGraph { get; set; } = string.Empty;

    public string Codec { get; set; } = "aac";

    /// <summary>Gets or sets the rate this branch writes at when nothing better is known.</summary>
    public string Bitrate { get; set; } = "256k";

    /// <summary>
    /// Gets or sets how many channels come out of the filter graph, or 0 where that is not knowable.
    /// </summary>
    /// <remarks>
    /// The source's own rate is only comparable to this encode's when the two counts match, which is
    /// what <see cref="SidecarBitrate"/> asks. Worth measuring rather than assuming:
    /// <c>dialoguenhance</c> takes stereo and produces <b>3.0</b>, not stereo — jellyfin-ffmpeg
    /// 7.1.4, read back off the encoded file.
    /// </remarks>
    public int OutputChannels { get; set; }
}

public static class ChannelLayoutDetector
{
    /// <summary>
    /// One named layout: its channels in order, and the encoder that will carry them.
    /// </summary>
    /// <remarks>
    /// <paramref name="channels"/> is the layout's own decomposition, exactly as
    /// <c>ffmpeg -layouts</c> gives it, which is what says how many channels the stream must have
    /// and where the front centre sits in the order. <paramref name="outputChannels"/> is what
    /// <paramref name="codec"/> was measured to actually write for it — not what the filter
    /// produces, which is a different number wherever an encoder folds.
    /// </remarks>
    private sealed class LayoutSpec
    {
        public LayoutSpec(string name, string channels, string codec, int outputChannels)
        {
            Name = name;
            Channels = channels.Split(' ');
            Codec = codec;
            OutputChannels = outputChannels;
            Centre = Array.IndexOf(Channels, "FC");
        }

        /// <summary>Gets the layout's canonical spelling, which is what <c>pan</c> is handed.</summary>
        public string Name { get; }

        /// <summary>Gets the layout's channels, in the order the decoder hands them over.</summary>
        public string[] Channels { get; }

        /// <summary>Gets the encoder that carries this layout's channels, or folds it least badly.</summary>
        public string Codec { get; }

        /// <summary>Gets the channels <see cref="Codec"/> writes for this layout. Measured.</summary>
        public int OutputChannels { get; }

        /// <summary>Gets where the front centre sits in <see cref="Channels"/>, or -1 for none.</summary>
        public int Centre { get; }

        /// <summary>
        /// Gets the rate this layout is written at when the source's own cannot be followed.
        /// </summary>
        /// <remarks>
        /// eac3 keeps the 640k it has always used. AAC gets 64 kbps a channel, which is the rate a
        /// multichannel AAC track is normally carried at — 448k for 6.1, 512k for 7.1. Only ever
        /// reached through <c>Preset</c>, or through <c>Auto</c> on a source whose rate says nothing
        /// about this encode (see <see cref="SidecarBitrate"/>).
        /// </remarks>
        public string PresetBitrate => Codec == "eac3"
            ? "640k"
            : AacRate(OutputChannels);
    }

    private static LayoutSpec Layout(string name, string channels, string codec, int outputChannels) =>
        new(name, channels, codec, outputChannels);

    /// <summary>
    /// The AAC rate for a track of this many channels: 64 kbps each, and never below the 256k a
    /// stereo track has always been written at.
    /// </summary>
    private static string AacRate(int channels) =>
        FormattableString.Invariant($"{Math.Max(256, 64 * channels)}k");

    /// <summary>
    /// Every layout that needs an answer of its own: all the ones with a front centre, plus the one
    /// without that aac refuses.
    /// </summary>
    /// <remarks>
    /// Two measurements decide each row, both on jellyfin-ffmpeg 7.1.4 and both re-runnable with
    /// <c>scripts/check-filter-graphs.sh</c>.
    /// <para>
    /// <b>Which channels the layout holds</b> comes from <c>ffmpeg -layouts</c>. It is what
    /// <c>pan</c> is strict about on its <em>output</em> side — naming a channel the layout does not
    /// have fails the encode outright, which is how the inherited 6.1 spec (built as 6.1(back)'s
    /// channels) failed every 6.1 source. It is also the count this stream must have: a layout
    /// naming six channels in front of an eight-channel track is not that track's layout, whichever
    /// half is wrong.
    /// </para>
    /// <para>
    /// <b>Which encoder to hand it to</b> was measured by encoding a tone bed at every standard
    /// layout and reading the channel count back. jellyfin-ffmpeg's eac3 encoder lists nothing above
    /// 5.1 and folds anything larger without failing — 6.1, 7.1 and 7.1.4 all came back as six
    /// channels of 5.1(side), exit 0, no warning. So eac3 is used for the layouts it lists, and aac
    /// for the rest, because aac carries 6.1 as seven channels and 7.1 as eight. The eight rows
    /// still on eac3 are the ones aac refuses outright (<c>Unsupported channel layout</c>) — every
    /// one of them a height layout no consumer decoder produces — and there eac3's fold is the
    /// least-bad answer available, because it is the only one that cannot fail. A user who really
    /// has such a source can name a lossless codec on the Custom profile.
    /// </para>
    /// <para>
    /// Layouts with no front centre are otherwise absent, because the two answers a row carries are
    /// both the default for them: nothing to lift in place, and aac carries every one of them.
    /// <c>cube</c> is the exception on both counts — no centre, and aac refuses it — so it is here
    /// with a centre of -1, which is what says the lift has nowhere to go.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, LayoutSpec> Layouts = new(StringComparer.OrdinalIgnoreCase)
    {
        // The layouts eac3 lists: every channel kept.
        ["3.0"] = Layout("3.0", "FL FR FC", "eac3", 3),
        ["4.0"] = Layout("4.0", "FL FR FC BC", "eac3", 4),
        ["3.1"] = Layout("3.1", "FL FR FC LFE", "eac3", 4),
        ["5.0"] = Layout("5.0", "FL FR FC BL BR", "eac3", 5),
        ["5.0(side)"] = Layout("5.0(side)", "FL FR FC SL SR", "eac3", 5),
        ["4.1"] = Layout("4.1", "FL FR FC LFE BC", "eac3", 5),
        ["5.1"] = Layout("5.1", "FL FR FC LFE BL BR", "eac3", 6),
        ["5.1(side)"] = Layout("5.1(side)", "FL FR FC LFE SL SR", "eac3", 6),

        // Above eac3's ceiling, and aac carries them whole.
        ["6.0"] = Layout("6.0", "FL FR FC BC SL SR", "aac", 6),
        ["hexagonal"] = Layout("hexagonal", "FL FR FC BL BR BC", "aac", 6),
        ["6.1"] = Layout("6.1", "FL FR FC LFE BC SL SR", "aac", 7),
        ["6.1(back)"] = Layout("6.1(back)", "FL FR FC LFE BL BR BC", "aac", 7),
        ["7.0"] = Layout("7.0", "FL FR FC BL BR SL SR", "aac", 7),
        ["7.0(front)"] = Layout("7.0(front)", "FL FR FC FLC FRC SL SR", "aac", 7),
        ["7.1"] = Layout("7.1", "FL FR FC LFE BL BR SL SR", "aac", 8),
        ["7.1(wide)"] = Layout("7.1(wide)", "FL FR FC LFE BL BR FLC FRC", "aac", 8),
        ["7.1(wide-side)"] = Layout("7.1(wide-side)", "FL FR FC LFE FLC FRC SL SR", "aac", 8),
        ["octagonal"] = Layout("octagonal", "FL FR FC BL BR BC SL SR", "aac", 8),
        ["hexadecagonal"] = Layout("hexadecagonal", "FL FR FC BL BR BC SL SR TFL TFC TFR TBL TBC TBR WL WR", "aac", 16),

        // Height layouts aac refuses. eac3 folds them rather than failing, and the boosted centre
        // survives the fold because `pan` runs first.
        ["3.1.2"] = Layout("3.1.2", "FL FR FC LFE TFL TFR", "eac3", 4),
        ["5.1.2"] = Layout("5.1.2", "FL FR FC LFE BL BR TFL TFR", "eac3", 6),
        ["5.1.4"] = Layout("5.1.4", "FL FR FC LFE BL BR TFL TFR TBL TBR", "eac3", 6),
        ["7.1.2"] = Layout("7.1.2", "FL FR FC LFE BL BR SL SR TFL TFR", "eac3", 6),
        ["7.1.4"] = Layout("7.1.4", "FL FR FC LFE BL BR SL SR TFL TFR TBL TBR", "eac3", 6),
        ["7.2.3"] = Layout("7.2.3", "FL FR FC LFE BL BR SL SR TFL TFR TBC LFE2", "eac3", 6),
        ["9.1.4"] = Layout("9.1.4", "FL FR FC LFE BL BR FLC FRC SL SR TFL TFR TBL TBR", "eac3", 6),
        ["22.2"] = Layout("22.2", "FL FR FC LFE BL BR FLC FRC BC SL SR TC TFL TFC TFR TBL TBC TBR LFE2 TSL TSR BFC BFL BFR", "eac3", 6),

        // No front centre, and aac refuses it anyway — so the lift has nowhere to go and the
        // carrier still has to be answered.
        ["cube"] = Layout("cube", "FL FR BL BR TFL TFR TBL TBR", "eac3", 4)
    };

    /// <summary>
    /// Layout names that more than one real layout answers to, differently.
    /// </summary>
    /// <remarks>
    /// Jellyfin reports a layout with its variant flattened away. Measured on this rig, one fixture
    /// per row: <c>6.1(back)</c>, <c>6.1(front)</c>, <c>6.0(front)</c>, <c>7.0(front)</c>,
    /// <c>7.1(wide)</c>, <c>7.1(wide-side)</c>, <c>3.0(back)</c>, <c>5.0(side)</c>,
    /// <c>5.1(side)</c> and <c>quad(side)</c> all come back under their bare names, while
    /// <c>hexagonal</c> and <c>6.0</c> come back as themselves.
    /// <para>
    /// For three of those names the flattening hides where the front centre is, or whether there is
    /// one at all: <c>6.1(front)</c> keeps its LFE at index 2, so a graph built on the name
    /// <c>6.1</c> would lift a subwoofer channel by 4 dB, and <c>3.0(back)</c> and
    /// <c>6.0(front)</c> have no centre to lift whatsoever. For two more — <c>7.0</c> and
    /// <c>7.1</c> — the centre is in the same place but the surrounding channels are not, so the
    /// sidecar would carry the right audio under the wrong labels.
    /// </para>
    /// <para>
    /// The 5.1, 5.0 and quad families are deliberately absent, and they are the ones that matter for
    /// cost: their only sibling is the <c>(side)</c> variant, which holds the same channels in the
    /// same order under two names. Measured both ways on a 5.1(side) source called <c>5.1</c> —
    /// identical levels through the graph, and eac3 writes the track as 5.1(side) whatever it was
    /// asked for. So the common case never pays for a probe, and no stored hash moves.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> Flattened = new(StringComparer.OrdinalIgnoreCase)
    {
        "3.0", "6.0", "6.1", "7.0", "7.1"
    };

    /// <summary>
    /// Whether this reported layout has to be confirmed against the decoder before it is used.
    /// </summary>
    public static bool NeedsDecodedLayout(string? channelLayout) =>
        !string.IsNullOrWhiteSpace(channelLayout) && Flattened.Contains(channelLayout.Trim());

    /// <summary>
    /// The layout ffmpeg gives a stream that names none, by channel count.
    /// </summary>
    /// <remarks>
    /// Not every source says what its channels are. Measured on this library's own fixtures: a
    /// six-channel PCM track and a seven-channel AAC one both reach Jellyfin with
    /// <c>ChannelLayout</c> null, and ffprobe calls them <c>unknown</c> at the stream and at the
    /// frame alike. There is no layout to disagree with there — so ffmpeg assigns its default for
    /// the count, and that default is not a guess about the file but the layout every consumer of
    /// it will use, this plugin's own <c>pan</c> included.
    /// <para>
    /// Read off jellyfin-ffmpeg 7.1.4 by handing it raw PCM at each count, which is a stream that
    /// carries no layout by construction. Only the counts whose default has a front centre are
    /// here. Four is deliberately absent even though its default (4.0) has one: <c>quad</c> is at
    /// least as common at that count and puts BL where 4.0 puts FC, so four channels is the one
    /// place where the default would be a coin toss about the centre itself. Three is absent
    /// because this build's default for it is 2.1, which has no centre at all.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<int, string> DefaultLayouts = new()
    {
        [5] = "5.0",
        [6] = "5.1",
        [7] = "6.1",
        [8] = "7.1",
        [10] = "5.1.4",
        [12] = "7.1.4",
        [16] = "hexadecagonal"
    };

    /// <summary>
    /// The table's entry for this stream, or null where its layout is not one the table answers for.
    /// </summary>
    /// <remarks>
    /// A layout we cannot name is not guessed at from the channel count: <c>pan</c> treats an input
    /// channel that is not there as silence rather than as an error, so guessing 5.1 at a 5.1(side)
    /// source would silently empty the surrounds instead of failing. The count has to agree as
    /// well — the name arrives from whichever analyzer read the file and is not a promise about how
    /// many channels the decoder will hand the filter.
    /// </remarks>
    private static LayoutSpec? LayoutFor(AudioStreamInfo stream)
    {
        // One and two channels have no separate centre to lift; the stereo enhancer is the whole
        // technique there, and it takes mono happily.
        if (stream.Channels <= 2)
        {
            return null;
        }

        // A stream that names no layout is not a stream we disagree with about one. It gets
        // ffmpeg's default for its channel count, which is what the decoder will hand the filter.
        if (string.IsNullOrWhiteSpace(stream.ChannelLayout))
        {
            return DefaultLayouts.TryGetValue(stream.Channels, out var byCount)
                ? Layouts[byCount]
                : null;
        }

        return Layouts.TryGetValue(stream.ChannelLayout.Trim(), out var layout)
               && layout.Channels.Length == stream.Channels
            ? layout
            : null;
    }

    /// <summary>
    /// Which of the two techniques this track gets.
    /// </summary>
    /// <remarks>
    /// A layout with a front centre is lifted where it stands. Everything else — a stereo or mono
    /// track, a real layout with no centre such as quad or 6.1(front), and any layout whose name
    /// this build cannot place — goes to <c>dialoguenhance</c>, which separates the voice out of
    /// the stereo image and is the whole technique where there is no centre channel to raise.
    /// </remarks>
    public static DialogueBoostBranch DetectBranch(AudioStreamInfo stream) =>
        LayoutFor(stream) is { Centre: >= 0 }
            ? DialogueBoostBranch.CenterGainInPlace
            : DialogueBoostBranch.StereoDialogueEnhance;

    /// <summary>
    /// Whether this is a layout ffmpeg names, and one that has a front centre channel to lift.
    /// </summary>
    public static bool HasReliableFrontCenter(string channelLayout) =>
        !string.IsNullOrWhiteSpace(channelLayout)
        && Layouts.TryGetValue(channelLayout.Trim(), out var layout)
        && layout.Centre >= 0;

    /// <summary>
    /// The encoder and rate for a filter that leaves the channel count alone, and how many channels
    /// that encoder will actually write.
    /// </summary>
    /// <remarks>
    /// The four profiles that are not Dialogue Boost — Night Mode, Speech, EBU R128, and anything
    /// unrecognised — all run filters that preserve the channel count (measured: dynaudnorm,
    /// speechnorm and loudnorm each return 6, 7 and 8 channels for 5.1, 6.1 and 7.1). They asked for
    /// aac at 256k regardless, and aac refuses a height layout outright: a 5.1.4 source failed the
    /// encode with <c>Unsupported channel layout "5.1.4"</c> on all three of them. They also claimed
    /// the source's channel count as their output, which for those layouts was a count of channels
    /// nothing wrote.
    /// <para>
    /// So the carrier is the same question the centre branch asks, minus the lift, and it is
    /// answered from the same table. A layout the table does not name keeps aac, which takes every
    /// standard layout up to eight channels bar the height ones.
    /// </para>
    /// </remarks>
    public static BranchExecutionSpec CarrierFor(AudioStreamInfo stream)
    {
        var layout = LayoutFor(stream);
        return new BranchExecutionSpec
        {
            Codec = layout?.Codec ?? "aac",
            Bitrate = layout?.PresetBitrate ?? AacRate(stream.Channels),
            OutputChannels = layout?.OutputChannels ?? stream.Channels
        };
    }

    public static BranchExecutionSpec BuildDialogueBoostBranchSpec(AudioStreamInfo stream, double centerGainDb)
    {
        var branch = DetectBranch(stream);
        var spec = new BranchExecutionSpec { Branch = branch };

        // Convert dB gain to linear multiplier: 10^(gainDb / 20.0)
        double linearGain = Math.Pow(10.0, centerGainDb / 20.0);
        string gainStr = linearGain.ToString("0.######", CultureInfo.InvariantCulture);

        switch (branch)
        {
            case DialogueBoostBranch.CenterGainInPlace:
            {
                var layout = LayoutFor(stream)!;
                spec.Codec = layout.Codec;
                spec.Bitrate = layout.PresetBitrate;
                spec.OutputChannels = layout.OutputChannels;

                // A `pan` whose coefficients are all 1 is not a matrix at all: ffmpeg takes it as a
                // channel *remap*, and a remap goes by name. Measured on a 7.1(wide) source called
                // 7.1 — `c0=c0|…|c7=c7` came back with FLC and FRC at -inf and everything else 6 dB
                // down, while the same graph with one non-unity coefficient carried all eight
                // channels through untouched. So a gain of 0 dB is not a quiet no-op, it is the one
                // shape of this graph that can lose channels. There is nothing to scale at unity
                // anyway, so nothing names a layout.
                if (gainStr == "1")
                {
                    spec.FilterGraph = "alimiter=limit=0.891";
                    break;
                }

                // By position, never by name. The layout string is what an analyzer *called* the
                // stream, and the two analyzers do not agree: Jellyfin reports every 6-channel
                // AC-3/E-AC-3 track in this library as "5.1" while ffmpeg decodes all 481 of them
                // as 5.1(side). Naming BL and BR at a 5.1(side) input asks `pan` for two channels
                // that are not there, and `pan` answers with silence and no warning — measured on
                // jellyfin-ffmpeg 7.1.4, and measured again in the sidecars that shipped: the
                // surrounds came back at -102 dB against a source carrying -41 dB.
                //
                // c0..cN are the same channels in the same order whichever of the two names is
                // right, because both layouts order them identically, so an index cannot point at
                // a channel the decoder did not supply. The table still decides *which* layouts
                // have a centre worth lifting and where in the order it sits.
                string channels = string.Join(
                    '|',
                    layout.Channels.Select((_, i) => i == layout.Centre
                        ? FormattableString.Invariant($"c{i}={gainStr}*c{i}")
                        : FormattableString.Invariant($"c{i}=c{i}")));

                // The table's own spelling, not the stream's: the lookup is case-insensitive and
                // `pan` is not.
                spec.FilterGraph = $"pan={layout.Name}|{channels},alimiter=limit=0.891";
                break;
            }

            case DialogueBoostBranch.StereoDialogueEnhance:
            default:
            {
                spec.Codec = "aac";
                spec.Bitrate = "256k";
                // Three, whatever went in: the filter keeps a stereo pair and puts the lifted voice
                // in a centre channel of its own. Measured on jellyfin-ffmpeg 7.1.4, for a stereo
                // source and for a six-channel one alike.
                spec.OutputChannels = 3;

                // A multichannel track reaches this filter as stereo because the filter asks for
                // stereo, and libswresample does that fold from the layout the *decoder* reports —
                // which is the one thing here that cannot be the wrong name for this stream. The
                // graph this replaced wrote the fold out by hand, `FL=0.9*FL+0.7*BL+1.1*FC+0.5*LFE`,
                // and so carried the same hazard: a channel the input does not have is silence, not an
                // error. Measured on a 6.0(front) bed — FL FR FLC FRC SL SR — that graph named four
                // channels the layout has none of and dropped the other four, writing 0.9*FL and
                // 0.9*FR and nothing else.
                spec.FilterGraph = "dialoguenhance=original=1:enhance=1:voice=2,alimiter=limit=0.891";
                break;
            }
        }

        return spec;
    }
}
