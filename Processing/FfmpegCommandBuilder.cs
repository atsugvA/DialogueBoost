using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

public class FfmpegCommandBuilder
{
    /// <summary>
    /// The languages of this item that this profile is set to process.
    /// </summary>
    /// <remarks>
    /// Its own method because the answer decides something before the command does: a profile whose
    /// languages match nothing in a file has nothing to write, and asking here rather than letting
    /// the build throw is what tells a *skip* from a *failure*. Recorded as a failure it read as
    /// work outstanding, was retried every run, and never succeeded.
    /// An empty <c>ProcessLanguages</c> means every language, which is the unconfigured default.
    /// </remarks>
    public static List<string> TargetLanguages(
        List<AudioStreamInfo> sourceAudioStreams,
        BaseProfileConfig profileConfig)
    {
        var available = sourceAudioStreams
            .Select(s => s.Language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (profileConfig.ProcessLanguages is null || profileConfig.ProcessLanguages.Count == 0)
        {
            return available;
        }

        // Normalised on both sides, so a profile set to `ger`, `deu` or `de` means German in a file
        // tagged any of the three (see LanguageCodes).
        var wanted = LanguageCodes.NormalizeAll(profileConfig.ProcessLanguages);
        return available.Where(lang => LanguageCodes.Covers(wanted, lang)).ToList();
    }

    /// <summary>
    /// Builds the encode for one profile. The destination is not part of it — see
    /// <see cref="FfmpegCommandSpec"/>.
    /// </summary>
    /// <remarks>
    /// The sidecar holds the tracks this profile produced and nothing else. It used to copy every
    /// original in alongside them, to satisfy an inherited claim that an unprocessed language would
    /// otherwise be unreachable. Measured against 10.11.11, that claim is false: an original that
    /// was never copied is still selected and played straight from the source file. What the
    /// copies bought was the same tracks offered twice in the audio menu and 3–5 GB per movie per
    /// profile.
    /// </remarks>
    public static FfmpegCommandSpec BuildCommand(
        string sourceVideoPath,
        List<AudioStreamInfo> sourceAudioStreams,
        BaseProfileConfig profileConfig)
    {
        if (sourceAudioStreams == null || sourceAudioStreams.Count == 0)
        {
            throw new ArgumentException("Source must have at least one audio stream.", nameof(sourceAudioStreams));
        }

        var targetLanguages = TargetLanguages(sourceAudioStreams, profileConfig);
        if (targetLanguages.Count == 0)
        {
            // The caller is expected to have asked first — an empty answer is a fact about the
            // item and this profile, not a failure, and ItemProcessor records it as one.
            throw new InvalidOperationException("No matching languages to process for profile.");
        }

        var args = new List<string> { "-y" }; // Overwrite output file if exists

        args.Add("-i");
        args.Add(sourceVideoPath);

        // Which language the source itself starts on, which is the one worth claiming when this
        // profile is the one that claims anything.
        AudioStreamInfo defaultSourceStream =
            sourceAudioStreams.FirstOrDefault(s => s.IsDefault) ?? sourceAudioStreams[0];

        var processed = new List<(AudioStreamInfo Source, int OutIndex, string Language)>();
        foreach (var lang in targetLanguages)
        {
            // The widest track of a language is the one worth processing: a 5.1 mix has a centre
            // channel to lift, its own stereo downmix has not.
            var candidateStream = sourceAudioStreams
                .Where(s => string.Equals(s.Language, lang, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Channels)
                .FirstOrDefault() ?? defaultSourceStream;

            processed.Add((candidateStream, processed.Count, lang));
            args.Add("-map");
            args.Add(FormattableString.Invariant($"0:a:{candidateStream.AudioIndex}"));
        }

        int defaultOutputIndex = DefaultOutputIndex(profileConfig, processed, defaultSourceStream);

        foreach (var (srcStream, outIdx, lang) in processed)
        {
            var spec = ProfileExecutionSpec(profileConfig, srcStream);

            args.Add(FormattableString.Invariant($"-filter:a:{outIdx}"));
            args.Add(spec.FilterGraph);
            args.Add(FormattableString.Invariant($"-c:a:{outIdx}"));
            args.Add(spec.Codec);
            args.Add(FormattableString.Invariant($"-b:a:{outIdx}"));
            args.Add(SidecarBitrate.Resolve(profileConfig, srcStream, spec));
            args.Add(FormattableString.Invariant($"-metadata:s:a:{outIdx}"));
            args.Add(FormattableString.Invariant($"title={profileConfig.SidecarNamingMarker}"));
            args.Add(FormattableString.Invariant($"-metadata:s:a:{outIdx}"));
            args.Add(FormattableString.Invariant($"language={lang}"));
            args.Add(FormattableString.Invariant($"-disposition:a:{outIdx}"));
            args.Add(outIdx == defaultOutputIndex ? "default" : "0");
        }

        // Format is always matroska (.mka). The path itself is the caller's to name.
        args.Add("-f");
        args.Add("matroska");

        return new FfmpegCommandSpec(args, targetLanguages, processed.Count);
    }

    /// <summary>
    /// Which output stream carries the container's <c>default</c> disposition, or -1 for none.
    /// </summary>
    /// <remarks>
    /// The container flag is the weaker half of the mechanism: Jellyfin ignores it outright on a
    /// single-stream external file and honours it per stream on a multi-stream one, while the
    /// filename's <c>.default</c> token claims the track at any stream count. So the flag is
    /// what says *which* language of a multi-language sidecar is the one, and the filename is what
    /// says the sidecar claims anything at all.
    /// </remarks>
    private static int DefaultOutputIndex(
        BaseProfileConfig profileConfig,
        List<(AudioStreamInfo Source, int OutIndex, string Language)> processed,
        AudioStreamInfo defaultSourceStream)
    {
        if (!profileConfig.SetAsDefaultTrack || processed.Count == 0)
        {
            return -1;
        }

        int matched = processed.FindIndex(
            p => string.Equals(p.Language, defaultSourceStream.Language, StringComparison.OrdinalIgnoreCase));

        return matched >= 0 ? processed[matched].OutIndex : processed[0].OutIndex;
    }

    /// <summary>What one profile does to one source track: the filter graph, the codec, the rate.</summary>
    private static BranchExecutionSpec ProfileExecutionSpec(BaseProfileConfig profileConfig, AudioStreamInfo srcStream)
    {
        switch (profileConfig)
        {
            case DialogueBoostProfile dbProfile:
                return ChannelLayoutDetector.BuildDialogueBoostBranchSpec(srcStream, dbProfile.CenterChannelGainDb);

            case NightModeProfile nmProfile:
                return Carrying(srcStream, FormattableString.Invariant(
                    $"dynaudnorm=m={nmProfile.DynaudnormGain.ToString("0.######", CultureInfo.InvariantCulture)},alimiter=limit=0.891"));

            case SpeechProfile spProfile:
                return Carrying(srcStream, FormattableString.Invariant(
                    $"speechnorm=p={spProfile.Peak.ToString("0.######", CultureInfo.InvariantCulture)},alimiter=limit=0.891"));

            case Ebur128Profile ebProfile:
                return Carrying(srcStream, string.Concat(
                    "loudnorm=I=", ebProfile.I.ToString("0.######", CultureInfo.InvariantCulture),
                    ":TP=", ebProfile.TP.ToString("0.######", CultureInfo.InvariantCulture),
                    ":LRA=", ebProfile.LRA.ToString("0.######", CultureInfo.InvariantCulture),
                    ",alimiter=limit=0.891"));

            case CustomProfile custProfile:
                // The one profile whose output channel count nobody here knows: the filter graph is
                // the user's, and `pan` and `channelmap` are as available to them as `volume` is.
                // Leaving it at 0 is what makes Auto fall back to their own preset rather than
                // match a rate against a channel count it cannot check.
                return new BranchExecutionSpec
                {
                    FilterGraph = string.IsNullOrWhiteSpace(custProfile.FilterString) ? "anull" : custProfile.FilterString,
                    Codec = string.IsNullOrWhiteSpace(custProfile.Codec) ? "aac" : custProfile.Codec,
                    Bitrate = string.IsNullOrWhiteSpace(custProfile.Bitrate) ? "256k" : custProfile.Bitrate
                };

            default:
                return Carrying(srcStream, "anull");
        }
    }

    /// <summary>
    /// A profile whose filter leaves the channel count alone, given the encoder that will carry it.
    /// </summary>
    /// <remarks>
    /// dynaudnorm, speechnorm and loudnorm each hand back what they were given — measured at 5.1,
    /// 6.1 and 7.1 — so the only open question is what will encode that, and it is the same question
    /// the Dialogue Boost branch answers. Asking aac for everything, which is what these three used
    /// to do, failed a 5.1.4 source outright on all of them.
    /// </remarks>
    private static BranchExecutionSpec Carrying(AudioStreamInfo srcStream, string filterGraph)
    {
        var spec = ChannelLayoutDetector.CarrierFor(srcStream);
        spec.FilterGraph = filterGraph;
        return spec;
    }
}
