using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DialogueBoost.Analysis;

namespace Jellyfin.Plugin.DialogueBoost.Output;

public static class SidecarNamer
{
    /// <summary>
    /// Jellyfin's own filename flags: a token it reads as something other than a track name.
    /// </summary>
    private static readonly HashSet<string> ReservedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "dvd", "part", "pt", "disc", "disk",
        "default", "forced", "foreign", "sdh", "cc", "hi"
    };

    /// <summary>
    /// Whether Jellyfin would read this marker out of the filename as something other than the
    /// track's name.
    /// </summary>
    /// <remarks>
    /// The language half used to be nine hard-written codes and their two-letter forms, which is
    /// both more than needed and less: <c>ebu</c> is a real ISO code, one letter from a marker
    /// somebody would plausibly type for the EBU R128 profile, and the list did not have it.
    /// <see cref="LanguageCodes.IsLanguageCode"/> answers for every language instead.
    /// </remarks>
    private static bool IsReserved(string marker) =>
        ReservedFlags.Contains(marker) || LanguageCodes.IsLanguageCode(marker);

    /// <summary>
    /// What this profile's track for this source is called. Which folder it goes in is
    /// <see cref="SidecarPlacement"/>'s answer.
    /// </summary>
    /// <remarks>
    /// The name starts with the source's own, because that is how Jellyfin decides a file is one of
    /// the video's tracks — in either folder it reads them from.
    /// <paramref name="claimsDefaultTrack"/> adds Jellyfin's own <c>.default</c> filename token,
    /// which is how a track says playback should start on it. The container's disposition flag is
    /// the weaker mechanism and cannot do this job alone: on a single-stream external file Jellyfin
    /// ignores it outright, while the filename token is honoured at any stream count. At most
    /// one profile may write this name — see
    /// <see cref="Configuration.PluginConfiguration.ClaimsDefaultTrack"/>.
    /// </remarks>
    public static string FileName(string sourceVideoPath, string sidecarNamingMarker, bool claimsDefaultTrack = false)
    {
        if (string.IsNullOrWhiteSpace(sourceVideoPath))
        {
            throw new ArgumentException("Source video path cannot be empty.", nameof(sourceVideoPath));
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourceVideoPath);
        string sanitizedMarker = SanitizeMarker(sidecarNamingMarker);
        string claim = claimsDefaultTrack ? ".default" : string.Empty;

        return $"{fileNameWithoutExtension}.{sanitizedMarker}{claim}.mka";
    }

    /// <summary>
    /// Every name this profile could have written for the source, claimed or not.
    /// </summary>
    /// <remarks>
    /// For the places that delete rather than write. Which name is current depends on a setting
    /// that may have changed since the file was written, and a sidecar left under the other name is
    /// a track in the audio menu that nothing will ever clean up.
    /// </remarks>
    public static IEnumerable<string> CandidateNames(string sourceVideoPath, string sidecarNamingMarker)
    {
        yield return FileName(sourceVideoPath, sidecarNamingMarker, claimsDefaultTrack: false);
        yield return FileName(sourceVideoPath, sidecarNamingMarker, claimsDefaultTrack: true);
    }

    /// <summary>The prefix of every temporary file this plugin writes.</summary>
    private const string TempPrefix = ".dialogueboost-tmp-";

    /// <summary>The temporary files the plugin wrote before 1.1: the target's name plus a suffix.</summary>
    private static readonly Regex LegacyTempName = new(@"\.mka\.tmp_[0-9a-f]{32}\.mka$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Where a track is encoded or copied before it is published under <paramref name="targetPath"/>:
    /// beside it, so that publishing is a rename on one filesystem, and under a name Jellyfin never
    /// takes for a track.
    /// </summary>
    /// <remarks>
    /// The name used to be the target's plus a suffix — <c>film.Dialogue Boost.default.mka.tmp_….mka</c>
    /// — and Jellyfin matches an external track by the video's name as a prefix and its extension,
    /// so it listed a half-written file as an audio track of that video, and from its
    /// <c>.default</c> token as the default one. Measured: a copy left under that name was served as
    /// external stream 0 with <c>IsDefault</c> set, while the same file named
    /// <c>.dialogueboost-tmp-….mka</c> was not listed at all. It still ends in <c>.mka</c>, because
    /// that is how ffmpeg chooses the container.
    /// </remarks>
    public static string TempPathBeside(string targetPath) =>
        Path.Combine(Path.GetDirectoryName(targetPath) ?? string.Empty, $"{TempPrefix}{Guid.NewGuid():N}.mka");

    /// <summary>
    /// Whether a file is a temporary one this plugin wrote, under either naming.
    /// </summary>
    public static bool IsTemporary(string fileName) =>
        (fileName.StartsWith(TempPrefix, StringComparison.Ordinal) && fileName.EndsWith(".mka", StringComparison.Ordinal))
        || LegacyTempName.IsMatch(fileName);

    public static bool IsMarkerValid(string marker, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            errorMessage = "Naming marker cannot be empty.";
            return false;
        }

        string trimmed = marker.Trim();
        if (IsReserved(trimmed))
        {
            errorMessage = $"Naming marker '{trimmed}' collides with Jellyfin reserved keyword.";
            return false;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (trimmed.Any(c => invalidChars.Contains(c)))
        {
            errorMessage = $"Naming marker '{trimmed}' contains invalid filename characters.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    public static string SanitizeMarker(string marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return "DialogueBoost";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var chars = marker.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray();
        string sanitized = new string(chars).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "DialogueBoost";
        }

        return IsReserved(sanitized) ? $"{sanitized}_Boost" : sanitized;
    }
}
