using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

public class ProcessResult
{
    /// <summary>How many trailing lines of stderr say what went wrong, and how much of them.</summary>
    /// <remarks>
    /// Measured on a real filtergraph failure: the last five lines are 393 characters and carry the
    /// three parser errors and both "Error opening output" lines — the whole of what ffmpeg had to
    /// say about it, out of 2,349 bytes.
    /// </remarks>
    private const int ErrorLines = 5;
    private const int ErrorChars = 500;

    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public bool Success => ExitCode == 0;

    /// <summary>
    /// The part of stderr that says what went wrong.
    /// </summary>
    /// <remarks>
    /// ffmpeg reports a failure on its last few lines and spends everything before them describing
    /// the input, so the tail is the part worth keeping and the head is not. It used to be kept
    /// whole: a failed encode put 2,267 bytes of version string, build configuration and library
    /// versions into <c>SkipReason</c>, which the config page then rendered verbatim into a table
    /// cell. <c>-hide_banner</c> takes that to 1,046; this takes it to the error itself.
    /// </remarks>
    public string ErrorTail()
    {
        var lines = StandardError
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Trim().Length > 0)
            .ToList();

        string tail = string.Join('\n', lines.Skip(Math.Max(0, lines.Count - ErrorLines)));

        return tail.Length > ErrorChars ? tail[^ErrorChars..] : tail;
    }
}

/// <summary>
/// Runs the encoder binaries. Every ffmpeg and ffprobe the plugin starts goes through here, so the
/// argument handling, the pipe draining and the cancellation behaviour are decided once.
/// </summary>
public class ProcessRunner
{
    private readonly IEncoderTools _encoderTools;
    private readonly ILogger<ProcessRunner> _logger;
    private int _identityLogged;

    public ProcessRunner(IEncoderTools encoderTools, ILogger<ProcessRunner> logger)
    {
        _encoderTools = encoderTools;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the ffmpeg binary Jellyfin encodes with.
    /// </summary>
    public string GetFfmpegPath() => _encoderTools.FfmpegPath;

    /// <summary>
    /// Resolves the ffprobe binary Jellyfin probes with.
    /// </summary>
    public string GetFfprobePath() => _encoderTools.FfprobePath;

    /// <summary>
    /// Runs ffmpeg. <c>-hide_banner</c> is not the caller's to ask for: the banner is a page of
    /// version string, build configuration and library versions that no caller here has ever wanted,
    /// and it is what a failure carried into the state database and onto the config page.
    /// </summary>
    public Task<ProcessResult> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        RunAsync("ffmpeg", GetFfmpegPath(), arguments.Prepend("-hide_banner").ToList(), cancellationToken, timeout);

    public Task<ProcessResult> RunFfprobeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        RunAsync("ffprobe", GetFfprobePath(), arguments, cancellationToken, timeout);

    private async Task<ProcessResult> RunAsync(
        string toolName,
        string toolPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        LogEncoderIdentityOnce();
        _logger.LogInformation("Executing {Tool}: {Path} {Arguments}", toolName, toolPath, ForLog(arguments));

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();

        // Both pipes are drained as they fill. A probe of a file with many streams outgrows the
        // pipe buffer, and a process that fills a pipe nobody is reading never exits.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdOutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stdErrBuilder.AppendLine(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {toolName} process at: {toolPath}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
        {
            cts.CancelAfter(timeout.Value);
        }

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{Tool} execution cancelled or timed out. Terminating process tree...", toolName);
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error terminating {Tool} process tree.", toolName);
            }

            throw;
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdOutBuilder.ToString(),
            StandardError = stdErrBuilder.ToString()
        };
    }

    /// <summary>
    /// Names the binaries this plugin is driving, once, the first time it drives one.
    /// </summary>
    /// <remarks>
    /// This is what the deleted startup check was for, at the only moment it can be right: the
    /// encoder paths are empty until Jellyfin publishes them, so anything asked at plugin start
    /// gets the PATH fallback and reports a binary no encode will ever use. The version comes
    /// from Jellyfin's own probe of the encoder, so nothing is spawned to ask.
    /// </remarks>
    private void LogEncoderIdentityOnce()
    {
        if (Interlocked.Exchange(ref _identityLogged, 1) != 0)
        {
            return;
        }

        _logger.LogInformation(
            "DialogueBoost: driving ffmpeg {Version:l} at '{FfmpegPath:l}', ffprobe at '{FfprobePath:l}'.",
            _encoderTools.EncoderVersion?.ToString() ?? "of unreported version",
            _encoderTools.FfmpegPath,
            _encoderTools.FfprobePath);
    }

    /// <summary>
    /// Renders an argument list for a human reading the log. Display only — the process is started
    /// from the list itself, so nothing here has to round-trip through a shell. Quotes are escaped
    /// rather than passed through: a path that contains one would otherwise read in the log as two
    /// arguments, which is the confusion this whole change is about.
    /// </summary>
    private static string ForLog(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(Quoted));

    private static string Quoted(string argument)
    {
        bool needsQuotes = argument.Length == 0
            || argument.Any(char.IsWhiteSpace)
            || argument.Contains('"', StringComparison.Ordinal);

        return needsQuotes
            ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument;
    }
}
