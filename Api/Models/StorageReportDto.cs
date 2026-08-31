using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// Whether the folders the selection covers can actually take a sidecar.
/// </summary>
/// <remarks>
/// Checked per folder rather than per volume: on an ntfs3 media volume each release folder carries
/// its own Linux mode, so the volume can be perfectly writable while the folder that matters is not
///.
/// </remarks>
public class StorageReportDto
{
    /// <summary>
    /// Gets or sets the account Jellyfin writes as.
    /// </summary>
    public string ServiceUser { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many folders were probed.
    /// </summary>
    public int FoldersChecked { get; set; }

    /// <summary>
    /// Gets or sets how many of them accepted a file.
    /// </summary>
    public int FoldersWritable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether there were more folders than the check looks at.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Gets or sets the folders that refused, each with the reason in full.
    /// </summary>
    public IReadOnlyList<StorageProblemDto> Problems { get; set; } = Array.Empty<StorageProblemDto>();
}

/// <summary>
/// One folder that cannot take a sidecar.
/// </summary>
public class StorageProblemDto
{
    /// <summary>
    /// Gets or sets the folder.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which kind of refusal it was — missing, read-only, denied, or out of space.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the reason, in a sentence that names the path, the account and what to look at.
    /// </summary>
    public string Explanation { get; set; } = string.Empty;
}
