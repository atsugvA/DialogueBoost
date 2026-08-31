namespace Jellyfin.Plugin.DialogueBoost.Configuration;

/// <summary>
/// Mode for triggering Jellyfin library refresh after publishing a sidecar file.
/// </summary>
public enum RefreshMode
{
    /// <summary>
    /// Accumulates processed items and refreshes the library in a single batch after the run finishes.
    /// Recommended for bulk runs to avoid parent directory rescan storms.
    /// </summary>
    BatchedAfterRun,

    /// <summary>
    /// Refreshes the Jellyfin item immediately after its sidecar file is published.
    /// Recommended for single-item or folder-scoped manual runs.
    /// </summary>
    PerItemImmediate
}
