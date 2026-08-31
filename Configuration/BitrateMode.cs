namespace Jellyfin.Plugin.DialogueBoost.Configuration;

/// <summary>
/// How a profile decides the bitrate of the track it writes.
/// </summary>
/// <remarks>
/// Three permanent choices, none of them a migration path away from the others. The plugin used to
/// have only the middle one, unnamed: <c>eac3 @ 640k</c> for the centre-gain branch and
/// <c>aac @ 256k</c> for the two stereo ones, with the source deciding nothing. Measured on this
/// library — 595 embedded audio streams, every one of them reporting a rate — the common case is a
/// <b>384 kbps source re-encoded at 640 kbps</b>, and one case is a 256 kbps source re-encoded at
/// 640. A lossy re-encode is bounded by what reached it; above the source's own rate the only thing
/// that shrinks is the additional loss, which is already inaudible there. The extra bits are paid
/// in full on disk.
/// </remarks>
public enum BitrateMode
{
    /// <summary>
    /// Follow the source where that means something, and fall back to <see cref="Preset"/> where it
    /// does not.
    /// </summary>
    /// <remarks>
    /// Not "copy the number". Two of the three dialogue-boost branches change the channel count, and
    /// a rate quoted for 5.1 means nothing once the output is stereo; a lossless source has no rate
    /// worth matching at all. See <see cref="Analysis.SidecarBitrate"/> for the rule.
    /// </remarks>
    Auto = 0,

    /// <summary>The profile's own default for the codec and channel layout it is about to write.</summary>
    Preset = 1,

    /// <summary>A rate the user named, whatever the source is.</summary>
    Manual = 2
}
