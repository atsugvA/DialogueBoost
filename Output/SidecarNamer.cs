using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Where this profile's sidecar belongs beside its source.
    /// </summary>
    /// <remarks>
    /// <paramref name="claimsDefaultTrack"/> adds Jellyfin's own <c>.default</c> filename token,
    /// which is how a track says playback should start on it. The container's disposition flag is
    /// the weaker mechanism and cannot do this job alone: on a single-stream external file Jellyfin
    /// ignores it outright, while the filename token is honoured at any stream count. At most
    /// one profile may write this name — see
    /// <see cref="Configuration.PluginConfiguration.ClaimsDefaultTrack"/>.
    /// </remarks>
    public static string GetSidecarPath(string sourceVideoPath, string sidecarNamingMarker, bool claimsDefaultTrack = false)
    {
        if (string.IsNullOrWhiteSpace(sourceVideoPath))
        {
            throw new ArgumentException("Source video path cannot be empty.", nameof(sourceVideoPath));
        }

        string directory = Path.GetDirectoryName(sourceVideoPath) ?? string.Empty;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourceVideoPath);
        string sanitizedMarker = SanitizeMarker(sidecarNamingMarker);
        string claim = claimsDefaultTrack ? ".default" : string.Empty;

        string sidecarFileName = $"{fileNameWithoutExtension}.{sanitizedMarker}{claim}.mka";
        return Path.Combine(directory, sidecarFileName);
    }

    /// <summary>
    /// Every name this profile could have written beside the source, claimed or not.
    /// </summary>
    /// <remarks>
    /// For the two places that delete rather than write. Which name is current depends on a setting
    /// that may have changed since the file was written, and a sidecar left under the other name is
    /// a track in the audio menu that nothing will ever clean up.
    /// </remarks>
    public static IEnumerable<string> CandidatePaths(string sourceVideoPath, string sidecarNamingMarker)
    {
        yield return GetSidecarPath(sourceVideoPath, sidecarNamingMarker, claimsDefaultTrack: false);
        yield return GetSidecarPath(sourceVideoPath, sidecarNamingMarker, claimsDefaultTrack: true);
    }

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
