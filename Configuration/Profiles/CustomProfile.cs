namespace Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;

public class CustomProfile : BaseProfileConfig
{
    public override string Id => "custom";

    public override string Name => "Custom Filter";

    /// <summary>
    /// Custom FFmpeg audio filter graph string.
    /// </summary>
    public string FilterString { get; set; } = string.Empty;

    /// <summary>
    /// Custom output audio codec (default: "aac").
    /// </summary>
    public string Codec { get; set; } = "aac";

    /// <summary>
    /// Custom audio bitrate (default: "256k").
    /// </summary>
    public string Bitrate { get; set; } = "256k";

    public CustomProfile()
    {
        Enabled = false;
        SidecarNamingMarker = "Custom Track";
    }
}
