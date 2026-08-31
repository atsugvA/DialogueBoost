using System;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Integration;

/// <summary>
/// The ffmpeg and ffprobe binaries a run should use. This is the plugin's only contact with
/// Jellyfin's media encoder.
/// </summary>
public interface IEncoderTools
{
    /// <summary>Gets the path to the ffmpeg binary. Never empty — see <see cref="IsConfigured"/>.</summary>
    string FfmpegPath { get; }

    /// <summary>Gets the path to the ffprobe binary. Never empty — see <see cref="IsConfigured"/>.</summary>
    string FfprobePath { get; }

    /// <summary>Gets the encoder version Jellyfin probed, or <c>null</c> before it has.</summary>
    Version? EncoderVersion { get; }

    /// <summary>
    /// Gets a value indicating whether Jellyfin has resolved its own encoder yet. False means the
    /// paths above are names to look up on <c>PATH</c>, not the binaries Jellyfin itself uses.
    /// </summary>
    bool IsConfigured { get; }
}

/// <summary>
/// Reads the binaries off <see cref="IMediaEncoder"/>, which is where Jellyfin publishes the paths
/// it has validated for itself.
/// </summary>
/// <remarks>
/// Both paths are read per call, never cached: the plugin's hosted services start before Jellyfin
/// sets them, so anything that captured them at construction would hold the fallback for the life
/// of the process.
/// <para>
/// A configured path is used as given — no existence check. Silently swapping to a different ffmpeg
/// build when the configured one looks unreachable is the real hazard: builds differ in
/// which filters they carry, and every profile here is a filter graph. A path Jellyfin set and the
/// process cannot start fails loudly, naming the path.
/// </para>
/// </remarks>
public sealed class JellyfinEncoderTools : IEncoderTools
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<JellyfinEncoderTools> _logger;
    private int _fallbackWarned;

    public JellyfinEncoderTools(IMediaEncoder mediaEncoder, ILogger<JellyfinEncoderTools> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public string FfmpegPath => Resolve(_mediaEncoder.EncoderPath, "ffmpeg");

    public string FfprobePath => Resolve(_mediaEncoder.ProbePath, "ffprobe");

    public Version? EncoderVersion => _mediaEncoder.EncoderVersion;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath) &&
        !string.IsNullOrWhiteSpace(_mediaEncoder.ProbePath);

    private string Resolve(string? configuredPath, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        // Once, not per call: this is read on every encode and every probe.
        if (System.Threading.Interlocked.Exchange(ref _fallbackWarned, 1) == 0)
        {
            _logger.LogWarning(
                "DialogueBoost: Jellyfin has not published its encoder paths yet; falling back to '{Name:l}' on PATH. "
                + "That may be a different ffmpeg build than the one Jellyfin encodes with.",
                fallbackName);
        }

        return fallbackName;
    }
}
