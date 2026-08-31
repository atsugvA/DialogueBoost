using Jellyfin.Plugin.DialogueBoost.Configuration;

namespace Jellyfin.Plugin.DialogueBoost.Analysis;

/// <summary>
/// Information about an audio stream in the source media container.
/// </summary>
public class AudioStreamInfo
{
    /// <summary>
    /// Stream index within the container (0-indexed).
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Audio stream index relative to audio streams only (0-indexed).
    /// </summary>
    public int AudioIndex { get; set; }

    /// <summary>
    /// ISO language code (e.g. "rus", "eng", "und").
    /// </summary>
    public string Language { get; set; } = "und";

    /// <summary>
    /// Stream title or description metadata.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Codec name (e.g. "ac3", "dts", "aac", "eac3", "flac").
    /// </summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>
    /// Number of audio channels (e.g. 1, 2, 6, 8).
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// String description of channel layout (e.g. "5.1", "5.1(side)", "7.1", "stereo", "mono").
    /// </summary>
    public string ChannelLayout { get; set; } = string.Empty;

    /// <summary>
    /// Bits per second the source encoded this track at, or 0 where nothing reported one.
    /// </summary>
    /// <remarks>
    /// What <see cref="BitrateMode.Auto"/> follows. Measured across this library, all 595 embedded
    /// streams report one through Jellyfin, so nothing extra has to be probed to know it; the
    /// ffprobe fallback path finds it only where the container stores it, and 0 is a real answer
    /// there rather than a missing one.
    /// </remarks>
    public int BitrateBps { get; set; }

    /// <summary>
    /// True if stream has disposition 'default'.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// True if this stream comes from an external audio sidecar file.
    /// </summary>
    public bool IsExternal { get; set; }
}
