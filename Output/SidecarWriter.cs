using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Output;

public class SidecarWriter
{
    private readonly ProcessRunner _processRunner;
    private readonly SourceStreamAnalyzer _streamAnalyzer;
    private readonly ILogger<SidecarWriter> _logger;

    public SidecarWriter(
        ProcessRunner processRunner,
        SourceStreamAnalyzer streamAnalyzer,
        ILogger<SidecarWriter> logger)
    {
        _processRunner = processRunner;
        _streamAnalyzer = streamAnalyzer;
        _logger = logger;
    }

    /// <summary>
    /// Encodes audio sidecar to a temp file, verifies it, then atomically publishes via rename.
    /// </summary>
    public async Task<string> ProcessAndWriteSidecarAsync(
        string targetSidecarPath,
        FfmpegCommandSpec commandSpec,
        bool verifyBeforePublish,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(targetSidecarPath) ?? string.Empty;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempSidecarPath = $"{targetSidecarPath}.tmp_{Guid.NewGuid():N}.mka";

        try
        {
            _logger.LogInformation("Encoding sidecar audio to temporary file: {TempPath}", tempSidecarPath);

            var result = await _processRunner
                .RunFfmpegAsync(commandSpec.ArgumentsWritingTo(tempSidecarPath), cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"FFmpeg failed with exit code {result.ExitCode}:\n{result.ErrorTail()}");
            }

            if (!File.Exists(tempSidecarPath))
            {
                throw new FileNotFoundException("FFmpeg succeeded but temporary sidecar file was not created.", tempSidecarPath);
            }

            var fileInfo = new FileInfo(tempSidecarPath);
            if (fileInfo.Length == 0)
            {
                throw new InvalidOperationException("Generated temporary sidecar file is 0 bytes.");
            }

            if (verifyBeforePublish)
            {
                _logger.LogInformation("Verifying temporary sidecar file with ffprobe: {TempPath}", tempSidecarPath);
                var probedStreams = await _streamAnalyzer.ProbeWithFfprobeAsync(tempSidecarPath, cancellationToken).ConfigureAwait(false);

                // What the command asked for is what has to be in there. The check used to be
                // "at least two streams", enforcing an inherited claim that a single-stream
                // external file makes the embedded tracks unselectable — measured false on
                // 10.11.11, where every embedded track stayed selectable beside a one-stream
                // sidecar. Counting against the spec catches what that never could: an
                // encode that dropped a track and still exited 0.
                if (probedStreams.Count != commandSpec.TotalOutputStreams)
                {
                    throw new InvalidOperationException(
                        $"Sidecar file verification failed: container holds {probedStreams.Count} audio stream(s), " +
                        $"and the command asked for {commandSpec.TotalOutputStreams}.");
                }

                _logger.LogInformation("Verification passed. Sidecar contains {Count} audio streams.", probedStreams.Count);
            }

            _logger.LogInformation("Publishing sidecar file atomically to: {TargetPath}", targetSidecarPath);
            File.Move(tempSidecarPath, targetSidecarPath, overwrite: true);

            return targetSidecarPath;
        }
        catch (Exception ex)
        {
            // Stopping the run is not a failure of the item, and it does not read like one either.
            // 881cd46 settled the accounting — a cancelled encode writes no Failed record and the
            // task reports Cancelled — but the log still answered a Stop button with one stack
            // trace per job in flight, which is what a broken plugin looks like. The temp file is
            // still cleaned up below; that is the part that matters.
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Stopped before finishing {TargetPath:l}; removing the temporary file.", targetSidecarPath);
            }
            else
            {
                _logger.LogError(ex, "Error while processing/writing sidecar file. Cleaning up temp file if present.");
            }

            if (File.Exists(tempSidecarPath))
            {
                try
                {
                    File.Delete(tempSidecarPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to delete temporary sidecar file: {TempPath}", tempSidecarPath);
                }
            }

            throw;
        }
    }
}
