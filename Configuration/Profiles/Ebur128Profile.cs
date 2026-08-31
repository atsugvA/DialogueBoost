namespace Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;

public class Ebur128Profile : BaseProfileConfig
{
    public override string Id => "ebur128";

    public override string Name => "EBU R128 Standard";

    /// <summary>
    /// Integrated loudness target in LUFS (default: -23.0).
    /// </summary>
    public double I { get; set; } = -23.0;

    /// <summary>
    /// Maximum True Peak limit in dBTP (default: -2.0).
    /// </summary>
    public double TP { get; set; } = -2.0;

    /// <summary>
    /// Loudness Range target in LU (default: 7.0).
    /// </summary>
    public double LRA { get; set; } = 7.0;

    public Ebur128Profile()
    {
        Enabled = false;
        SidecarNamingMarker = "EBU R128";
    }
}
