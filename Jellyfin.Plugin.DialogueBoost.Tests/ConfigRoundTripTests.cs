using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// The configuration survives a trip through Jellyfin's own serializer unchanged.
/// </summary>
/// <remarks>
/// Jellyfin persists plugin configuration with <see cref="XmlSerializer"/>, which fills a
/// collection property by calling its getter and <c>Add</c>-ing into whatever is already there —
/// it never replaces the list the initialiser built. Every list here therefore has to ship empty:
/// a property that starts non-empty grows by its own default on every load.
/// </remarks>
public class ConfigRoundTripTests
{
    private static PluginConfiguration RoundTrip(PluginConfiguration config)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var buffer = new StringWriter();
        serializer.Serialize(buffer, config);
        return (PluginConfiguration)serializer.Deserialize(new StringReader(buffer.ToString()))!;
    }

    private static PluginConfiguration Load(string xml) =>
        (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration))
            .Deserialize(new StringReader(xml))!;

    /// <summary>A configuration document in the shape 1.0.0.0 wrote: the codes as a list.</summary>
    private static string LegacyDocument(string codes) =>
        "<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        "<DialogueBoostProfile><ProcessLanguages>" + codes + "</ProcessLanguages>" +
        "</DialogueBoostProfile></PluginConfiguration>";

    /// <summary>Saving without changing anything must not change anything.</summary>
    [Fact]
    public void ShippedDefaultsSurviveARoundTrip()
    {
        var loaded = RoundTrip(new PluginConfiguration());

        Assert.Equal(new[] { "eng" }, loaded.DialogueBoostProfile.ProcessLanguages);
        Assert.Equal(new[] { "eng" }, loaded.NightModeProfile.ProcessLanguages);
        Assert.Equal(new[] { "eng" }, loaded.SpeechProfile.ProcessLanguages);
        Assert.Equal(new[] { "eng" }, loaded.Ebur128Profile.ProcessLanguages);
        Assert.Equal(new[] { "eng" }, loaded.CustomProfile.ProcessLanguages);
    }

    /// <summary>Ten saves are the same document as one — a list that grows re-encodes the library each time.</summary>
    [Fact]
    public void RepeatedSavesDoNotGrowTheLanguageList()
    {
        var config = new PluginConfiguration();
        for (var i = 0; i < 10; i++)
        {
            config = RoundTrip(config);
        }

        Assert.Equal(new[] { "eng" }, config.DialogueBoostProfile.ProcessLanguages);
    }

    /// <summary>A language the user replaced stays replaced; the default does not come back beside it.</summary>
    [Fact]
    public void AChosenLanguageDoesNotReadBackBesideTheDefault()
    {
        var config = new PluginConfiguration();
        config.DialogueBoostProfile.ProcessLanguages = new List<string> { "deu" };

        Assert.Equal(new[] { "deu" }, RoundTrip(config).DialogueBoostProfile.ProcessLanguages);
    }

    /// <summary>An emptied field means every language, and has to survive being written down.</summary>
    [Fact]
    public void AnEmptiedLanguageFieldStaysEmpty()
    {
        var config = new PluginConfiguration();
        config.DialogueBoostProfile.ProcessLanguages = new List<string>();

        Assert.Empty(RoundTrip(config).DialogueBoostProfile.ProcessLanguages);
    }

    /// <summary>
    /// A 1.0.0.0 document stored these as a list. The language the user chose there is what the
    /// upgraded plugin uses — reverting it to the shipped default would write English tracks
    /// beside a German library on the first run after an update.
    /// </summary>
    [Fact]
    public void ALanguageStoredByTheOldBuildIsCarriedForward()
    {
        var loaded = Load(LegacyDocument("<string>deu</string>"));

        Assert.Equal(new[] { "deu" }, loaded.DialogueBoostProfile.ProcessLanguages);
    }

    /// <summary>The old build doubled its own default on every load; reading the document repairs that.</summary>
    [Fact]
    public void ADoubledListFromTheOldBuildIsReadOnce()
    {
        var loaded = Load(LegacyDocument("<string>eng</string><string>eng</string><string>eng</string><string>deu</string>"));

        Assert.Equal(new[] { "eng", "deu" }, loaded.DialogueBoostProfile.ProcessLanguages);
    }

    /// <summary>Saving retires the old element, so it cannot outrank the value on the next load.</summary>
    [Fact]
    public void SavingAnUpgradedDocumentRetiresTheOldElement()
    {
        var upgraded = Load(LegacyDocument("<string>deu</string>"));
        upgraded.DialogueBoostProfile.ProcessLanguages = new List<string> { "fra" };

        Assert.Equal(new[] { "fra" }, RoundTrip(upgraded).DialogueBoostProfile.ProcessLanguages);
    }

    /// <summary>A document the current build wrote never carries the old element at all.</summary>
    [Fact]
    public void TheCurrentBuildDoesNotWriteTheOldElement()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var buffer = new StringWriter();
        serializer.Serialize(buffer, new PluginConfiguration());

        Assert.Contains("<ProcessLanguagesCsv>eng</ProcessLanguagesCsv>", buffer.ToString());
        Assert.DoesNotContain("<ProcessLanguages>", buffer.ToString());
        Assert.DoesNotContain("<ProcessLanguages />", buffer.ToString());
    }

    /// <summary>Every list on the document, not just the one that was caught.</summary>
    [Fact]
    public void NoListPropertyGrowsAcrossASave()
    {
        var config = RoundTrip(RoundTrip(new PluginConfiguration()));

        Assert.Empty(config.SelectedLibraries);
        Assert.Empty(config.WatchedByUserIds);
        Assert.Empty(config.TrackSelectionRules.Languages);
        Assert.Empty(config.TrackSelectionRules.AllowedCodecs);
        Assert.Single(config.DialogueBoostProfile.ProcessLanguages);
    }
}
