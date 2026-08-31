using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// What came back from looking at one item's audio: the tracks that qualify, and whether the file
/// could be read at all.
/// </summary>
/// <remarks>
/// The two used to be one empty list, and they are not the same answer. "I read it and nothing
/// qualifies" is a durable fact about the item that a run can write down and the page can trust;
/// "I could not read it" is a broken file or an unreachable folder, and recording that as *nothing
/// to process* would be a worse lie than saying nothing at all.
/// </remarks>
/// <param name="Streams">The tracks the rules accept. Empty is a real answer when readable.</param>
/// <param name="Readable">Whether the item's audio could be read at all.</param>
public readonly record struct AudioCandidates(List<AudioStreamInfo> Streams, bool Readable)
{
    /// <summary>Gets the answer for a file whose tracks could not be read.</summary>
    public static AudioCandidates Unreadable => new(new List<AudioStreamInfo>(), false);

    /// <summary>Gets whether the rules accepted nothing, on a file that was read.</summary>
    public bool NothingQualifies => Readable && Streams.Count == 0;
}

/// <summary>
/// The audio tracks of one item that the track-selection rules accept.
/// </summary>
public sealed class CandidateStreams
{
    private readonly SourceStreamAnalyzer _analyzer;
    private readonly ILogger<CandidateStreams> _logger;

    public CandidateStreams(SourceStreamAnalyzer analyzer, ILogger<CandidateStreams> logger)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    /// <summary>
    /// Reads the item's tracks and applies the rules.
    /// </summary>
    /// <remarks>
    /// Jellyfin's own <c>MediaStreams</c> first — it probed the file when it scanned it, and asking
    /// again costs a process per item. ffprobe is the fallback for an item Jellyfin has no stream
    /// list for, which is what a file that arrived without a scan looks like.
    /// </remarks>
    public async Task<AudioCandidates> ForAsync(
        BaseItem item,
        string sourcePath,
        TrackSelectionRulesConfig rules,
        CancellationToken cancellationToken)
    {
        List<AudioStreamInfo> all;
        try
        {
            var source = item.GetMediaSources(false)?.FirstOrDefault();
            all = source?.MediaStreams is not null
                ? SourceStreamAnalyzer.ExtractFromMediaStreams(source.MediaStreams)
                : new List<AudioStreamInfo>();

            if (all.Count == 0)
            {
                all = await _analyzer.ProbeWithFfprobeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the audio tracks of '{ItemName:l}' at {Path}.", item.Name, sourcePath);
            return AudioCandidates.Unreadable;
        }

        var candidates = SourceStreamAnalyzer.FilterCandidateStreams(all, rules);
        if (candidates.Count == 0)
        {
            _logger.LogInformation(
                "No audio track of '{ItemName:l}' matches the selection rules ({Count} looked at).", item.Name, all.Count);
        }

        await ConfirmFlattenedLayoutsAsync(candidates, item, sourcePath, cancellationToken).ConfigureAwait(false);
        return new AudioCandidates(candidates, true);
    }

    /// <summary>
    /// Asks the decoder what a layout really is, where the reported name cannot say.
    /// </summary>
    /// <remarks>
    /// Jellyfin reports a layout with its variant flattened off — <c>6.1(front)</c> arrives as
    /// <c>6.1</c>, measured — and for five of those names the variants disagree about where the
    /// front centre sits or whether there is one, so a graph built on the name lifts the wrong
    /// channel. ffmpeg's own reading is the one that matters, because ffmpeg is what decodes the
    /// file, so for those names alone it is asked.
    /// <para>
    /// One ffprobe per item, and only for an item whose layout is one of the five: 67 ms on a 518 MB
    /// source here, against a run that spends minutes encoding. Every 5.1 and stereo track — which
    /// is every track in this library — skips it entirely, so no stored hash moves and a run that
    /// finds everything current stays as fast as it was.
    /// </para>
    /// <para>
    /// A track is matched by its ordinal among the source's own audio streams, and only where the
    /// two readings agree on the channel count: disagreeing about that means one of them is not
    /// describing this stream, and the layout table refuses a count mismatch anyway.
    /// </para>
    /// </remarks>
    private async Task ConfirmFlattenedLayoutsAsync(
        List<AudioStreamInfo> candidates,
        BaseItem item,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!candidates.Any(s => ChannelLayoutDetector.NeedsDecodedLayout(s.ChannelLayout)))
        {
            return;
        }

        List<AudioStreamInfo> probed;
        try
        {
            probed = await _analyzer.ProbeWithFfprobeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The reported name is still the best answer available, and it is right for every
            // layout that has no differently-shaped sibling.
            _logger.LogWarning(
                ex, "Could not confirm the channel layouts of '{ItemName:l}' with ffprobe.", item.Name);
            return;
        }

        foreach (var candidate in candidates.Where(s => ChannelLayoutDetector.NeedsDecodedLayout(s.ChannelLayout)))
        {
            var match = probed.FirstOrDefault(
                p => p.AudioIndex == candidate.AudioIndex && p.Channels == candidate.Channels);

            if (match is null || string.IsNullOrWhiteSpace(match.ChannelLayout)
                || string.Equals(match.ChannelLayout, candidate.ChannelLayout, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _logger.LogInformation(
                "'{ItemName:l}' track {Index} is reported as {Reported:l} and decodes as {Decoded:l}; using the decoder's.",
                item.Name, candidate.AudioIndex, candidate.ChannelLayout, match.ChannelLayout);
            candidate.ChannelLayout = match.ChannelLayout;
        }
    }
}
