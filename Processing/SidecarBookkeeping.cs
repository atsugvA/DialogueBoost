using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// Brings what is on disk back in line with what the record says, before anything decides whether
/// an encode is needed.
/// </summary>
/// <remarks>
/// Both cases are the same mistake seen from two sides: the record names a path that is no longer
/// where the sidecar belongs. Renaming the video moves where it belongs; renaming the profile's
/// marker changes what it is called. Neither is a reason to encode again, and neither may be
/// ignored — the second would otherwise leave a stale track in the audio menu forever.
/// </remarks>
public sealed class SidecarBookkeeping
{
    private readonly ProcessingStateRepository _stateRepository;
    private readonly ILogger<SidecarBookkeeping> _logger;

    public SidecarBookkeeping(ProcessingStateRepository stateRepository, ILogger<SidecarBookkeeping> logger)
    {
        _stateRepository = stateRepository;
        _logger = logger;
    }

    public async Task ReconcileAsync(ProcessedItemRecord existing, string itemId, string sourcePath, string sidecarPath)
    {
        // Ordinal, not culture-aware: on Linux two paths differing only in case are two files.
        if (!existing.SourcePath.Equals(sourcePath, StringComparison.Ordinal))
        {
            _logger.LogInformation("Source moved from {OldPath} to {NewPath}; following it.", existing.SourcePath, sourcePath);
            await _stateRepository.UpdateSourcePathIfRenamedAsync(itemId, sourcePath).ConfigureAwait(false);
            Move(existing.SidecarPath, sidecarPath);
        }

        if (!string.IsNullOrWhiteSpace(existing.SidecarPath) &&
            !existing.SidecarPath.Equals(sidecarPath, StringComparison.Ordinal) &&
            File.Exists(existing.SidecarPath))
        {
            Delete(existing.SidecarPath);
        }
    }

    private void Move(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || !File.Exists(oldPath) || File.Exists(newPath))
        {
            return;
        }

        try
        {
            File.Move(oldPath, newPath);
            _logger.LogInformation("Renamed sidecar {OldPath} → {NewPath}", oldPath, newPath);
        }
        catch (Exception ex)
        {
            // Not fatal: the encode below writes the sidecar where it now belongs.
            _logger.LogWarning(ex, "Could not rename sidecar {OldPath} → {NewPath}; it will be written again.", oldPath, newPath);
        }
    }

    private void Delete(string stalePath)
    {
        try
        {
            File.Delete(stalePath);
            _logger.LogInformation("Deleted the sidecar left at the old name: {OldPath}", stalePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the sidecar left at {OldPath}.", stalePath);
        }
    }
}
