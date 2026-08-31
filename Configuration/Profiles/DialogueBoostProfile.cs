namespace Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;

public class DialogueBoostProfile : BaseProfileConfig
{
    public override string Id => "dialogue-boost";

    public override string Name => "Dialogue Boost";

    /// <summary>
    /// Center channel boost gain in dB (default: 4.0 dB).
    /// Used by CenterGainInPlace branch.
    /// </summary>
    public double CenterChannelGainDb { get; set; } = 4.0;

    public DialogueBoostProfile()
    {
        Enabled = true;
        SidecarNamingMarker = "Dialogue Boost";

        // The one profile that ships claiming the default track, so a fresh install plays the
        // boosted track without anyone selecting it by hand. At most one profile may claim it —
        // see PluginConfiguration.ClaimsDefaultTrack — and this is the one that resolves first.
        SetAsDefaultTrack = true;
    }
}
