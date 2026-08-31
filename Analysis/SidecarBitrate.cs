using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;

namespace Jellyfin.Plugin.DialogueBoost.Analysis;

/// <summary>
/// What rate one profile writes one track at.
/// </summary>
/// <remarks>
/// The plugin used to answer this with a constant per branch, and the constant was usually bigger
/// than the source. Measured over this library: 309 streams are <c>ac3 5.1 @ 384k</c> and every one
/// of them was being re-encoded at 640k. Over the 31 sidecars that existed on 2026-08-28 the two
/// M6 changes together — dropping the copied originals, and matching the source's rate — take
/// 10.96 GB to 4.33 GB.
/// </remarks>
public static class SidecarBitrate
{
    /// <summary>
    /// The lowest rate worth asking either encoder for.
    /// </summary>
    /// <remarks>
    /// Measured on jellyfin-ffmpeg 7.1.4 at 44.1 kHz: the eac3 encoder fails the frame outright at
    /// 32 kbps and below — <c>Error encoding a frame: Invalid argument</c> — for 3 channels and for
    /// 6 alike, and takes 40 kbps. The aac encoder refused nothing down to 8 kbps, so for that one
    /// this is a floor on sense rather than on the codec.
    /// </remarks>
    public const int MinKbps = 40;

    /// <summary>
    /// The highest rate worth asking either encoder for.
    /// </summary>
    /// <remarks>
    /// Measured the same way: the eac3 encoder refuses to open above this — <c>invalid bit rate.
    /// must be 2768 to 5644800 for this sample rate</c> — while aac accepted 6144 kbps and simply
    /// delivered less. No real source approaches it; the bound is here so a nonsense tag cannot
    /// fail an encode.
    /// </remarks>
    public const int MaxKbps = 5644;

    /// <summary>
    /// Codecs whose rate says nothing about how many bits the next encode needs.
    /// </summary>
    /// <remarks>
    /// A lossless source is not spending its bits on the same thing a lossy one is: matching
    /// TrueHD's 4 Mbps in eac3 would be absurd, and matching FLAC's would be arbitrary. <c>dts</c>
    /// is in here for a different reason — ffprobe reports DTS-HD MA and plain DTS under the same
    /// <c>codec_name</c>, so the pair cannot be told apart from what is captured, and the table's
    /// answer is the right one for both.
    /// </remarks>
    private static readonly HashSet<string> NoRateWorthMatching = new(StringComparer.OrdinalIgnoreCase)
    {
        "truehd", "mlp", "flac", "alac", "dts", "wavpack", "tta", "ape",
        "als", "mp4als", "ralf", "s302m", "shorten", "tak", "wmalossless"
    };

    /// <summary>
    /// Codec-name prefixes that mean the same thing as the set above, for families ffmpeg spells
    /// about thirty ways.
    /// </summary>
    /// <remarks>
    /// Found by running a PCM 5.1 fixture through the whole plugin rather than by reading:
    /// <c>pcm_s16le</c> is not <c>pcm</c>, so it missed the set, and 6 channels × 48 kHz × 16 bits
    /// is a real bitrate Jellyfin reports — 4,608,000 of them. Auto followed it, and an E-AC-3
    /// sidecar came out at 4608 kbps and 12 MB where every other one on that rig is 1.6 MB. The
    /// rate is a function of the sample format there, not of the content, which is exactly what
    /// this list means.
    /// </remarks>
    private static readonly string[] NoRatePrefixes = { "pcm_", "dsd_" };

    /// <summary>Whether this codec's own rate is a number about the audio rather than the format.</summary>
    private static bool RateWorthMatching(string codec) =>
        !NoRateWorthMatching.Contains(codec)
        && !NoRatePrefixes.Any(prefix => codec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The <c>-b:a</c> value for this profile, this source track, and this branch.
    /// </summary>
    public static string Resolve(BaseProfileConfig profile, AudioStreamInfo source, BranchExecutionSpec branch) =>
        profile.BitrateMode switch
        {
            BitrateMode.Manual => Kbps(profile.BitrateKbps),
            BitrateMode.Preset => branch.Bitrate,
            _ => Follows(source, branch) ? Kbps(source.BitrateBps / 1000) : branch.Bitrate
        };

    /// <summary>
    /// Whether the source's own rate is a number this encode can be measured against.
    /// </summary>
    /// <remarks>
    /// It is not, in three cases. The branch changed the channel count, and a rate quoted for 5.1
    /// says nothing about a stereo output — nor about a 3.0 one, which is what
    /// <c>dialoguenhance</c> produces from a stereo source (measured, jellyfin-ffmpeg 7.1.4). The
    /// source is lossless or uncompressed, where the rate is a fact about the sample format rather
    /// than about the audio. Or nothing reported a rate at all, which is what the ffprobe fallback
    /// sees on a container that does not store one.
    /// </remarks>
    private static bool Follows(AudioStreamInfo source, BranchExecutionSpec branch) =>
        branch.OutputChannels > 0
        && branch.OutputChannels == source.Channels
        && source.BitrateBps > 0
        && RateWorthMatching(source.Codec);

    private static string Kbps(int kbps) =>
        FormattableString.Invariant($"{Math.Clamp(kbps, MinKbps, MaxKbps)}k");
}
