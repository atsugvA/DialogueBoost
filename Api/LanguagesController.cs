using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DialogueBoost.Api;

/// <summary>
/// The language codes the page offers as a hint beside every "process these languages" field.
/// </summary>
/// <remarks>
/// Served rather than written into the page because a list the browser keeps its own copy of can
/// name a code the plugin does not map — and an unmapped code fails silently, since
/// <see cref="LanguageCodes.Normalize"/> hands one back unchanged and the rule then matches
/// nothing. Every entry here comes out of the same class the run compares with.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class LanguagesController : ControllerBase
{
    /// <summary>
    /// The languages worth naming, as ISO 639-1 because that is the shortest key into the rest.
    /// </summary>
    /// <remarks>
    /// A hint, not a constraint: the field takes any code, and this list is the ones a media
    /// library in Europe or North America actually meets. Ordered by English name below, not here.
    /// </remarks>
    private static readonly string[] Common =
    {
        "ar", "bg", "bs", "ca", "cs", "da", "de", "el", "en", "es", "et", "fa", "fi", "fr", "he",
        "hi", "hr", "hu", "id", "is", "it", "ja", "ko", "lt", "lv", "nl", "no", "pl", "pt", "ro",
        "ru", "sk", "sl", "sr", "sv", "th", "tr", "uk", "vi", "zh"
    };

    /// <summary>
    /// Each language's English name and every spelling of it this plugin accepts.
    /// </summary>
    [HttpGet("/Plugins/DialogueBoost/Languages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LanguageHint>> GetLanguages() =>
        Common
            .Select(code => new LanguageHint(EnglishName(code), LanguageCodes.SpellingsOf(code)))
            .Where(hint => hint.Name.Length > 0)
            .OrderBy(hint => hint.Name, StringComparer.Ordinal)
            .ToList();

    private static string EnglishName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>One language: what to call it, and what may be typed for it.</summary>
    /// <param name="Name">The language's English name, from ICU.</param>
    /// <param name="Codes">639-1 first, then 639-2/B where it differs, then 639-2/T.</param>
    public record LanguageHint(string Name, IReadOnlyList<string> Codes);
}
