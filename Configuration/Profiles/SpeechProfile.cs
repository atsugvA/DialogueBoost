namespace Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;

public class SpeechProfile : BaseProfileConfig
{
    public override string Id => "speech";

    public override string Name => "Speech / Podcast";

    /// <summary>
    /// Target peak value for speechnorm filter (default: 0.95).
    /// </summary>
    public double Peak { get; set; } = 0.95;

    public SpeechProfile()
    {
        Enabled = false;
        SidecarNamingMarker = "Speech Boost";
    }
}
