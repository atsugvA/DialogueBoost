using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// An ffmpeg invocation with everything decided except where it lands.
/// </summary>
/// <remarks>
/// The destination is deliberately absent. A sidecar is encoded to a temp file, verified, then
/// renamed into place, so the path ffmpeg writes to is not the path the caller asked for — and the
/// spec used to be built with the final path and then edited, by searching the flat command string
/// for that path in quotes and splicing the temp one over it. A source file whose own name
/// contained the sidecar's name rewrote the input instead. Here the destination cannot be baked in
/// early, because it is a parameter of the only method that produces a runnable list.
/// <para>
/// Arguments are a list because that is what survives: no quoting, so no path — Windows, spaces,
/// quotes, newlines — can end one argument and start another.
/// </para>
/// </remarks>
public sealed class FfmpegCommandSpec
{
    private readonly IReadOnlyList<string> _argumentsBeforeOutput;

    public FfmpegCommandSpec(
        IReadOnlyList<string> argumentsBeforeOutput,
        IReadOnlyList<string> processedLanguages,
        int totalOutputStreams)
    {
        _argumentsBeforeOutput = argumentsBeforeOutput;
        ProcessedLanguages = processedLanguages;
        TotalOutputStreams = totalOutputStreams;
    }

    /// <summary>Gets the languages this command will produce a processed track for.</summary>
    public IReadOnlyList<string> ProcessedLanguages { get; }

    /// <summary>Gets the number of audio streams the output container will hold.</summary>
    public int TotalOutputStreams { get; }

    /// <summary>
    /// The complete argument list, writing to <paramref name="outputPath"/>.
    /// </summary>
    public IReadOnlyList<string> ArgumentsWritingTo(string outputPath) =>
        _argumentsBeforeOutput.Append(outputPath).ToList();
}
