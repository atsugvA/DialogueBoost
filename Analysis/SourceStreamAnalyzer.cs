using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Processing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.DialogueBoost.Analysis;

public class SourceStreamAnalyzer
{
    private readonly ProcessRunner _processRunner;

    public SourceStreamAnalyzer(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    /// <summary>
    /// The source file's own audio tracks, in the order ffmpeg counts them.
    /// </summary>
    /// <remarks>
    /// External streams are skipped, and the ordinal counts only what is left: <c>-map 0:a:N</c>
    /// names the Nth audio stream *of the input file*, so a sidecar sitting beside it must not
    /// shift the numbering. Ordered by container index rather than by the order Jellyfin happens to
    /// list them, because that ordinal is what the mapping means.
    /// </remarks>
    public static List<AudioStreamInfo> ExtractFromMediaStreams(IEnumerable<MediaStream> streams)
    {
        var audioStreams = new List<AudioStreamInfo>();
        int audioIndexCounter = 0;

        foreach (var stream in streams.OrderBy(stream => stream.Index))
        {
            if (stream.Type != MediaStreamType.Audio || stream.IsExternal)
            {
                continue;
            }

            audioStreams.Add(new AudioStreamInfo
            {
                Index = stream.Index,
                AudioIndex = audioIndexCounter++,
                Language = LanguageCodes.Normalize(stream.Language),
                Title = stream.Title ?? string.Empty,
                Codec = stream.Codec ?? string.Empty,
                Channels = stream.Channels ?? 2,
                ChannelLayout = stream.ChannelLayout ?? string.Empty,
                BitrateBps = stream.BitRate ?? 0,
                IsDefault = stream.IsDefault,
                IsExternal = stream.IsExternal
            });
        }

        return audioStreams;
    }

    /// <summary>
    /// Asks ffprobe what audio streams a file holds.
    /// </summary>
    /// <remarks>
    /// The run goes through <see cref="ProcessRunner"/> rather than starting a process here. The
    /// local copy quoted the path into a flat argument string, so it failed on exactly the names
    /// ffmpeg's own arguments used to fail on, and it repeated the pipe-draining and
    /// cancellation handling that already existed one class away.
    /// </remarks>
    public async Task<List<AudioStreamInfo>> ProbeWithFfprobeAsync(string mediaPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException("Media file not found for ffprobe analysis", mediaPath);
        }

        var result = await _processRunner.RunFfprobeAsync(
            new[] { "-v", "quiet", "-print_format", "json", "-show_streams", "-select_streams", "a", mediaPath },
            cancellationToken).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException(
                $"ffprobe failed with exit code {result.ExitCode} for path: {mediaPath}");
        }

        return ParseFfprobeOutput(result.StandardOutput);
    }

    public static List<AudioStreamInfo> ParseFfprobeOutput(string jsonOutput)
    {
        var result = new List<AudioStreamInfo>();
        using var doc = JsonDocument.Parse(jsonOutput);

        if (!doc.RootElement.TryGetProperty("streams", out var streamsElement))
        {
            return result;
        }

        int audioIndexCounter = 0;
        foreach (var stream in streamsElement.EnumerateArray())
        {
            int index = stream.GetProperty("index").GetInt32();

            string language = LanguageCodes.Undetermined;
            string title = string.Empty;
            if (stream.TryGetProperty("tags", out var tags))
            {
                if (tags.TryGetProperty("language", out var langProp))
                {
                    // The raw container tag, which is where the two paths used to disagree: this
                    // one reads `ger` off the file while Jellyfin's own list reports `deu`.
                    language = LanguageCodes.Normalize(langProp.GetString());
                }
                if (tags.TryGetProperty("title", out var titleProp))
                {
                    title = titleProp.GetString() ?? string.Empty;
                }
            }

            string codec = stream.TryGetProperty("codec_name", out var codecProp) ? codecProp.GetString() ?? "" : "";
            int channels = stream.TryGetProperty("channels", out var chanProp) ? chanProp.GetInt32() : 2;
            string channelLayout = stream.TryGetProperty("channel_layout", out var layoutProp) ? layoutProp.GetString() ?? "" : "";

            // ffprobe reports it as a string, and only where the container stores it — matroska
            // often does not, which is why 0 is a real answer here and not a missing one.
            int bitrateBps = stream.TryGetProperty("bit_rate", out var rateProp)
                             && int.TryParse(rateProp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;

            bool isDefault = false;
            if (stream.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("default", out var defVal))
            {
                isDefault = defVal.GetInt32() == 1;
            }

            result.Add(new AudioStreamInfo
            {
                Index = index,
                AudioIndex = audioIndexCounter++,
                Language = language,
                Title = title,
                Codec = codec,
                Channels = channels,
                ChannelLayout = channelLayout,
                BitrateBps = bitrateBps,
                IsDefault = isDefault,
                IsExternal = false
            });
        }

        return result;
    }

    public static List<AudioStreamInfo> FilterCandidateStreams(
        List<AudioStreamInfo> sourceStreams,
        TrackSelectionRulesConfig rules)
    {
        var ruleLanguages = LanguageCodes.NormalizeAll(rules.Languages);

        return sourceStreams.Where(s =>
        {
            if (rules.ExcludeExternalTracks && s.IsExternal)
            {
                return false;
            }

            if (ruleLanguages.Count > 0 && !LanguageCodes.Covers(ruleLanguages, s.Language))
            {
                return false;
            }

            if (rules.MinChannels > 0 && s.Channels < rules.MinChannels)
            {
                return false;
            }

            if (rules.MaxChannels > 0 && s.Channels > rules.MaxChannels)
            {
                return false;
            }

            if (rules.AllowedCodecs.Count > 0 && !rules.AllowedCodecs.Contains(s.Codec, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rules.TitleRegexPattern))
            {
                try
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(s.Title, rules.TitleRegexPattern))
                    {
                        return false;
                    }
                }
                catch (System.ArgumentException)
                {
                    // Fix #17: Invalid regex pattern — treat as non-matching rather than crashing
                    return false;
                }
            }

            return true;
        }).ToList();
    }
}
