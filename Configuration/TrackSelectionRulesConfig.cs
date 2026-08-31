using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Configuration;

/// <summary>
/// Configuration rules for selecting candidate source audio tracks.
/// </summary>
public class TrackSelectionRulesConfig
{
    /// <summary>
    /// List of language codes to match (empty = all languages).
    /// </summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>
    /// Minimum allowed channel count for source track (0 = no minimum).
    /// </summary>
    public int MinChannels { get; set; } = 0;

    /// <summary>
    /// Maximum allowed channel count for source track (0 = no maximum).
    /// </summary>
    public int MaxChannels { get; set; } = 0;

    /// <summary>
    /// List of source audio codecs to include (empty = all codecs).
    /// </summary>
    public List<string> AllowedCodecs { get; set; } = new();

    /// <summary>
    /// Regex pattern to filter source track title (empty = match all).
    /// </summary>
    public string TitleRegexPattern { get; set; } = string.Empty;

    /// <summary>
    /// If true, ignores external audio tracks when inspecting source video candidates.
    /// </summary>
    public bool ExcludeExternalTracks { get; set; } = true;
}
