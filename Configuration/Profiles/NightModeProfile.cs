namespace Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;

public class NightModeProfile : BaseProfileConfig
{
    public override string Id => "night-mode";

    public override string Name => "Broadband Night Mode";

    /// <summary>
    /// Dynaudnorm maximum gain factor 'm' parameter (default: 15.0).
    /// </summary>
    public double DynaudnormGain { get; set; } = 15.0;

    public NightModeProfile()
    {
        Enabled = false;
        SidecarNamingMarker = "Broadband Night Mode";
    }
}
