using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// The same file reaches the plugin down two paths that disagree about its own tag. Measured on the
/// live server, 2026-08-28: ffprobe reads <c>ger</c> off <c>DB M1 Control (2020).mkv</c> while
/// Jellyfin reports that same stream as <c>deu</c>.
/// </summary>
public class LanguageCodesTests
{
    [Theory]
    [InlineData("ger", "deu")]
    [InlineData("GER", "deu")]
    [InlineData("deu", "deu")]
    [InlineData("de", "deu")]
    [InlineData("fre", "fra")]
    [InlineData("cze", "ces")]
    [InlineData("en", "eng")]
    [InlineData("pt-BR", "por")]
    public void Normalize_EverySpellingOfALanguageReachesOneCode(string written, string expected)
    {
        Assert.Equal(expected, LanguageCodes.Normalize(written));
    }

    /// <summary>
    /// An unknown tag still identifies a track, and two streams carrying the same unknown tag are
    /// still the same language as each other — so it is kept, not discarded.
    /// </summary>
    [Theory]
    [InlineData("und", "und")]
    [InlineData("", "und")]
    [InlineData(null, "und")]
    [InlineData("qqq", "qqq")]
    [InlineData("  ENG  ", "eng")]
    public void Normalize_WhatItCannotResolveComesBackLowercasedAndIntact(string? written, string expected)
    {
        Assert.Equal(expected, LanguageCodes.Normalize(written));
    }

    [Theory]
    [InlineData("ger", true)]
    [InlineData("deu", true)]
    [InlineData("de", true)]
    [InlineData("ebu", true)]
    [InlineData("Dialogue Boost", false)]
    [InlineData("EBU R128", false)]
    [InlineData("Boost", false)]
    [InlineData("german", false)]
    [InlineData("", false)]
    public void IsLanguageCode_AnswersForTheShapeOfACodeAndNothingElse(string token, bool expected)
    {
        Assert.Equal(expected, LanguageCodes.IsLanguageCode(token));
    }

    /* ── what the disagreement actually cost ─────────────────────────────────────────────────── */

    /// <summary>
    /// The M1 fixtures are tagged <c>ger</c>; a rule written <c>deu</c> skipped every one of them.
    /// </summary>
    [Fact]
    public void TrackRules_ARuleInOneSpellingMatchesATrackInTheOther()
    {
        var streams = new List<AudioStreamInfo>
        {
            new() { AudioIndex = 0, Language = "ger", Channels = 2, ChannelLayout = "stereo" }
        };

        var rules = new TrackSelectionRulesConfig { Languages = new List<string> { "deu" } };

        Assert.Single(SourceStreamAnalyzer.FilterCandidateStreams(streams, rules));
    }

    [Fact]
    public void ProfileLanguages_ARuleInOneSpellingMatchesATrackInTheOther()
    {
        var streams = new List<AudioStreamInfo>
        {
            new() { AudioIndex = 0, Language = "deu", Channels = 6, ChannelLayout = "5.1" }
        };

        var profile = new DialogueBoostProfile { ProcessLanguages = new List<string> { "ger" } };

        Assert.Equal(new[] { "deu" }, FfmpegCommandBuilder.TargetLanguages(streams, profile));
    }

    /// <summary>
    /// The two analyzer paths give one answer for one file. The ffprobe path reads the container's
    /// own tag; the MediaStreams path reads what Jellyfin made of it.
    /// </summary>
    [Fact]
    public void BothAnalyzerPaths_AgreeAboutAFileTaggedGer()
    {
        const string Ffprobe = """
            {"streams":[{"index":1,"codec_name":"ac3","channels":2,"channel_layout":"stereo",
                         "tags":{"language":"ger"},"disposition":{"default":0}}]}
            """;

        var fromFfprobe = SourceStreamAnalyzer.ParseFfprobeOutput(Ffprobe);

        Assert.Equal("deu", Assert.Single(fromFfprobe).Language);
    }

    /// <summary>
    /// What the configuration page offers beside the language field. Every spelling it lists has to
    /// be one the rules actually accept — a code this class does not map is not rejected, it is
    /// compared as itself and matches nothing, which is the silent failure the hint exists to
    /// prevent.
    /// </summary>
    [Theory]
    [InlineData("de", "de", "ger", "deu")]
    [InlineData("ger", "de", "ger", "deu")]
    [InlineData("deu", "de", "ger", "deu")]
    [InlineData("fr", "fr", "fre", "fra")]
    [InlineData("zh", "zh", "chi", "zho")]
    public void SpellingsOf_ALanguageWithTwoThreeLetterCodes_ListsAllThree(
        string asked, string one, string two, string three)
    {
        Assert.Equal(new[] { one, two, three }, LanguageCodes.SpellingsOf(asked));
    }

    [Theory]
    [InlineData("en", "en", "eng")]
    [InlineData("eng", "en", "eng")]
    [InlineData("ru", "ru", "rus")]
    [InlineData("ja", "ja", "jpn")]
    public void SpellingsOf_ALanguageWithOne_DoesNotListItTwice(string asked, string one, string two)
    {
        Assert.Equal(new[] { one, two }, LanguageCodes.SpellingsOf(asked));
    }

    /// <summary>Every spelling the page offers has to normalise to the same language.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("cs")]
    [InlineData("nl")]
    [InlineData("el")]
    [InlineData("is")]
    [InlineData("fa")]
    [InlineData("ro")]
    [InlineData("sk")]
    [InlineData("sv")]
    [InlineData("uk")]
    public void SpellingsOf_EverySpellingNormalisesToTheSameLanguage(string code)
    {
        var spellings = LanguageCodes.SpellingsOf(code);
        var normalized = spellings.Select(LanguageCodes.Normalize).Distinct().ToList();

        Assert.Single(normalized);
        Assert.Equal(LanguageCodes.Normalize(code), normalized[0]);
    }

    /// <summary>A tag nothing maps still answers with itself, so the page never shows a blank.</summary>
    [Fact]
    public void SpellingsOf_AnUnknownTag_IsItsOwnOnlySpelling()
    {
        Assert.Equal(new[] { "zzz" }, LanguageCodes.SpellingsOf("zzz"));
        Assert.Equal(new[] { "und" }, LanguageCodes.SpellingsOf(null));
    }
}
