using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Jellyfin.Plugin.DialogueBoost.Analysis;

namespace Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;

/// <summary>
/// Base class for profile configurations.
/// </summary>
public abstract class BaseProfileConfig
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    public bool Enabled { get; set; }

    private string _processLanguages = DefaultProcessLanguages;

    /// <summary>The shipped value, and the only place it is written down.</summary>
    private const string DefaultProcessLanguages = "eng";

    /// <summary>
    /// ISO 639-2 or 639-1 language codes selected for processing (e.g. "eng", "spa").
    /// Empty list means process all candidate languages matching track selection rules.
    /// </summary>
    /// <remarks>
    /// Ships as <c>eng</c> rather than empty. Empty means <i>every</i> language, which on a
    /// dual-audio library writes one sidecar per language per item — a surprising amount of disk
    /// for a default nobody chose. One named language is the predictable start; it is also the
    /// setting most likely to need changing, so the page names it in the first-run copy.
    ///
    /// <para><b>Not stored as a list</b>, and that is not a style choice.
    /// <see cref="System.Xml.Serialization.XmlSerializer"/> fills a collection property by calling
    /// its getter and <c>Add</c>-ing into whatever the initialiser already built — it never
    /// replaces it, and a setter is not consulted either. A list shipping as <c>eng</c> therefore
    /// grew by its own default on every load: <c>eng</c> read back as <c>eng, eng</c> and gained
    /// another on each save, a chosen <c>deu</c> read back as <c>eng, deu</c> — writing English
    /// tracks nobody asked for — and a field cleared to mean <i>every language</i> silently reverted
    /// to <c>eng</c>. A string is replaced rather than appended to, so all three states survive
    /// being written down; <c>ConfigRoundTripTests</c> holds every one of them.</para>
    /// </remarks>
    [XmlIgnore]
    public List<string> ProcessLanguages
    {
        get => LegacyProcessLanguages.Count > 0
            ? Distinct(LegacyProcessLanguages)
            : Split(_processLanguages);
        set
        {
            _processLanguages = string.Join(",", Distinct(value ?? new List<string>()));

            // The document has been written in the current shape; the old element has nothing left
            // to say and must not be carried forward, or it would outrank this value on every load.
            LegacyProcessLanguages.Clear();
        }
    }

    /// <summary>
    /// Gets or sets what the configuration document actually stores for
    /// <see cref="ProcessLanguages"/>: the same codes, comma-separated.
    /// </summary>
    /// <remarks>
    /// XML only. The page and the plugin API go on seeing <see cref="ProcessLanguages"/> as the
    /// list it always was.
    /// </remarks>
    [XmlElement("ProcessLanguagesCsv")]
    [JsonIgnore]
    public string ProcessLanguagesCsv
    {
        get => _processLanguages;
        set => _processLanguages = value ?? string.Empty;
    }

    /// <summary>
    /// Gets or sets the codes a 1.0.0.0 document stored as a list.
    /// </summary>
    /// <remarks>
    /// <b>Legacy — read once, never written.</b> Only a document from before the list became a
    /// string can carry these, and while it does they outrank the string, so an upgrade keeps the
    /// language the user chose instead of reverting to the shipped default. Reading them through
    /// <see cref="Distinct"/> is also what repairs a list the old build had already doubled. The
    /// first save clears it. Ships empty, so it has no default of its own to append.
    /// </remarks>
    [XmlArray("ProcessLanguages")]
    [XmlArrayItem("string")]
    [JsonIgnore]
    public List<string> LegacyProcessLanguages { get; set; } = new();

    /// <summary>Keeps the retired element out of every document the current build writes.</summary>
    public bool ShouldSerializeLegacyProcessLanguages() => LegacyProcessLanguages.Count > 0;

    private static List<string> Split(string csv) =>
        Distinct((csv ?? string.Empty).Split(','));

    private static List<string> Distinct(IEnumerable<string> codes)
    {
        var seen = new List<string>();
        foreach (var code in codes)
        {
            var trimmed = code?.Trim();
            if (!string.IsNullOrEmpty(trimmed) &&
                !seen.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(trimmed);
            }
        }

        return seen;
    }

    /// <summary>
    /// Human-readable marker appended to sidecar filename and used as audio track title.
    /// Example: "Dialogue Boost", "Broadband Night Mode".
    /// </summary>
    public string SidecarNamingMarker { get; set; } = string.Empty;

    /// <summary>
    /// If true, sets disposition default on the processed audio stream matching source default language.
    /// </summary>
    public bool SetAsDefaultTrack { get; set; } = false;

    /// <summary>
    /// Gets or sets how the bitrate of the written track is decided. Default is
    /// <see cref="Configuration.BitrateMode.Auto"/>, which follows the source.
    /// </summary>
    public BitrateMode BitrateMode { get; set; } = BitrateMode.Auto;

    private int _bitrateKbps = 256;

    /// <summary>
    /// Gets or sets the rate in kbps used when <see cref="BitrateMode"/> is
    /// <see cref="Configuration.BitrateMode.Manual"/>. Ignored otherwise.
    /// </summary>
    /// <remarks>
    /// Bounded by what the encoders actually take — see
    /// <see cref="Analysis.SidecarBitrate"/>, where both ends were measured rather than assumed.
    /// </remarks>
    public int BitrateKbps
    {
        get => _bitrateKbps;
        set => _bitrateKbps = Math.Clamp(value, SidecarBitrate.MinKbps, SidecarBitrate.MaxKbps);
    }

    public RefreshMode RefreshMode { get; set; } = RefreshMode.BatchedAfterRun;

    public bool VerifyBeforePublish { get; set; } = true;
}
