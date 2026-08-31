using Jellyfin.Plugin.DialogueBoost.Configuration;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// At most one profile writes Jellyfin's <c>.default</c> filename token. Two sidecars both claiming
/// it is not a tie the plugin gets to break — Jellyfin takes the lowest external stream index, so
/// the winner would be whichever filename happened to sort first.
/// </summary>
public class DefaultTrackClaimTests
{
    private static PluginConfiguration WithClaims(bool dialogueBoost, bool nightMode, bool speech)
    {
        var config = new PluginConfiguration();
        config.DialogueBoostProfile.Enabled = true;
        config.DialogueBoostProfile.SetAsDefaultTrack = dialogueBoost;
        config.NightModeProfile.Enabled = true;
        config.NightModeProfile.SetAsDefaultTrack = nightMode;
        config.SpeechProfile.Enabled = true;
        config.SpeechProfile.SetAsDefaultTrack = speech;
        return config;
    }

    [Fact]
    public void OnlyOneProfileClaimsIt_EvenWhenSeveralAskFor()
    {
        var config = WithClaims(dialogueBoost: true, nightMode: true, speech: true);

        Assert.True(config.ClaimsDefaultTrack(config.DialogueBoostProfile));
        Assert.False(config.ClaimsDefaultTrack(config.NightModeProfile));
        Assert.False(config.ClaimsDefaultTrack(config.SpeechProfile));
    }

    [Fact]
    public void NobodyClaimsItWhenNobodyAsked()
    {
        var config = WithClaims(dialogueBoost: false, nightMode: false, speech: false);

        Assert.False(config.ClaimsDefaultTrack(config.DialogueBoostProfile));
        Assert.False(config.ClaimsDefaultTrack(config.NightModeProfile));
    }

    /// <summary>
    /// A profile that is switched off is not in the running, so the claim moves to the next one that
    /// asked — which is why the resolved claim, and not the switch, is what the hash carries.
    /// </summary>
    [Fact]
    public void ADisabledProfileHoldsNothing_AndTheClaimMovesOn()
    {
        var config = WithClaims(dialogueBoost: true, nightMode: true, speech: false);
        config.DialogueBoostProfile.Enabled = false;

        Assert.False(config.ClaimsDefaultTrack(config.DialogueBoostProfile));
        Assert.True(config.ClaimsDefaultTrack(config.NightModeProfile));
    }
}
