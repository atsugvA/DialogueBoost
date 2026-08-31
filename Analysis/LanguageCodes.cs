using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.DialogueBoost.Analysis;

/// <summary>
/// One answer to "what language is this track in", whichever spelling arrived.
/// </summary>
/// <remarks>
/// The same file reaches this plugin down two paths that disagree about its own tag.
/// <c>example.show.s02e05….mkv</c> carries <c>ger</c> in the container; Jellyfin reports that stream
/// as <c>deu</c>, and across the whole library returns only <c>deu</c>, <c>eng</c> and <c>fra</c> —
/// no <c>ger</c> at all. So <c>ExtractFromMediaStreams</c> sees Jellyfin's normalised value while
/// the ffprobe fallback reads the raw tag, and comparing either against the user's own rule as an
/// exact string means a rule saying <c>deu</c> silently skips a file tagged <c>ger</c>. Both are
/// ISO 639-2 for German: <c>deu</c> is the terminological code, <c>ger</c> the bibliographic one.
/// <para>
/// Normalising to the terminological code settles it in one place, for both paths and for the
/// rules. Two-letter 639-1 codes come along for free — a rule written <c>de</c> matches too —
/// because ICU already knows that mapping and maintaining a second table would only let it drift.
/// </para>
/// </remarks>
public static class LanguageCodes
{
    /// <summary>The language of a track nothing tagged.</summary>
    public const string Undetermined = "und";

    /// <summary>
    /// The twenty languages ISO 639-2 gives two codes: bibliographic on the left, terminological on
    /// the right.
    /// </summary>
    /// <remarks>
    /// A closed set — the list has not changed since 639-2 was published — and one ICU declines to
    /// map: measured, <c>CultureInfo.GetCultureInfo("ger").ThreeLetterISOLanguageName</c> is empty,
    /// as it is for every other code in this column. So the table is not a duplicate of what the
    /// platform knows; it is the part the platform does not.
    /// </remarks>
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi",
        ["arm"] = "hye",
        ["baq"] = "eus",
        ["bur"] = "mya",
        ["chi"] = "zho",
        ["cze"] = "ces",
        ["dut"] = "nld",
        ["fre"] = "fra",
        ["geo"] = "kat",
        ["ger"] = "deu",
        ["gre"] = "ell",
        ["ice"] = "isl",
        ["mac"] = "mkd",
        ["mao"] = "mri",
        ["may"] = "msa",
        ["per"] = "fas",
        ["rum"] = "ron",
        ["slo"] = "slk",
        ["tib"] = "bod",
        ["wel"] = "cym"
    };

    /// <summary>
    /// The ISO 639-2/T code for whatever was written, lowercase.
    /// </summary>
    /// <remarks>
    /// A tag that is not a language it recognises comes back lowercased and otherwise untouched,
    /// rather than being thrown away: an unknown tag still identifies a track, and two streams
    /// carrying the same unknown tag are still the same language as each other.
    /// </remarks>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Undetermined;
        }

        string trimmed = code.Trim().ToLowerInvariant();
        return Bibliographic.TryGetValue(trimmed, out var terminological)
            ? terminological
            : Terminological(trimmed) ?? trimmed;
    }

    /// <summary>
    /// Whether this token is a language code at all — which is what makes it unusable as a sidecar
    /// naming marker, because Jellyfin would read it out of the filename as the track's language.
    /// </summary>
    /// <remarks>
    /// Length-bounded because that is the shape of a code, and because it is the only shape ICU
    /// will answer for: <c>german</c> throws where <c>ger</c> resolves. Replaces a hand-written
    /// list of nine languages that missed, among others, <c>ebu</c> — a real code, and one letter
    /// away from a marker somebody would plausibly type.
    /// </remarks>
    public static bool IsLanguageCode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string trimmed = token.Trim();
        return trimmed.Length is >= 2 and <= 3
               && (Bibliographic.ContainsKey(trimmed) || Terminological(trimmed.ToLowerInvariant()) is not null);
    }

    /// <summary>
    /// The configured codes as they will actually be compared: normalised and de-duplicated.
    /// </summary>
    /// <remarks>
    /// Done once per rule rather than once per track, and it is why a user may write
    /// <c>ger</c>, <c>deu</c> or <c>de</c> and mean the same thing.
    /// </remarks>
    public static HashSet<string> NormalizeAll(IEnumerable<string>? codes) =>
        (codes ?? Enumerable.Empty<string>())
        .Select(Normalize)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every spelling of one language this plugin will accept, from any spelling of it.
    /// </summary>
    /// <remarks>
    /// The two-letter 639-1 code first, then the bibliographic 639-2/B where the language has one,
    /// then the terminological 639-2/T that everything normalises to — so German answers
    /// <c>de, ger, deu</c> and English answers <c>en, eng</c>. Duplicates are dropped rather than
    /// listed twice.
    /// <para>
    /// It exists so the configuration page can say which spellings a language takes without keeping
    /// its own copy of ISO 639. A list restated in the browser could name a code this class does
    /// not map, and an unmapped code is not an error here — <see cref="Normalize"/> hands it back
    /// lowercased — so the rule would simply never match, which is the exact confusion the hint is
    /// there to prevent.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> SpellingsOf(string? code)
    {
        string terminological = Normalize(code);
        var spellings = new List<string>();

        string? two = TwoLetter(terminological);
        if (two is not null)
        {
            spellings.Add(two);
        }

        foreach (var pair in Bibliographic)
        {
            if (string.Equals(pair.Value, terminological, StringComparison.Ordinal))
            {
                spellings.Add(pair.Key);
                break;
            }
        }

        if (!spellings.Contains(terminological, StringComparer.Ordinal))
        {
            spellings.Add(terminological);
        }

        return spellings;
    }

    /// <summary>The ISO 639-1 code for a 639-2/T one, or null where the language has none.</summary>
    private static string? TwoLetter(string terminological)
    {
        try
        {
            string two = CultureInfo.GetCultureInfo(terminological).TwoLetterISOLanguageName;
            return two.Length == 2 ? two.ToLowerInvariant() : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Whether a normalised set of codes covers this track's language.</summary>
    public static bool Covers(HashSet<string> normalizedCodes, string? language) =>
        normalizedCodes.Contains(Normalize(language));

    /// <summary>
    /// ICU's own 639-2/T answer, or null where it has none.
    /// </summary>
    /// <remarks>
    /// It answers for every 639-1 and 639-2/T code and for region-tagged names (<c>pt-BR</c> is
    /// <c>por</c>); it returns an empty string for a well-formed name it does not know, and throws
    /// for one it cannot parse at all. Both of those are the same "no" here.
    /// </remarks>
    private static string? Terminological(string code)
    {
        try
        {
            string three = CultureInfo.GetCultureInfo(code).ThreeLetterISOLanguageName;
            return string.IsNullOrWhiteSpace(three) ? null : three.ToLowerInvariant();
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
