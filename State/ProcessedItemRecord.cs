using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.State;

public class ProcessedItemRecord
{
    public string ItemId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public long SourceMTimeUtc { get; set; }
    public long SourceSizeBytes { get; set; }
    public string ParamsHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile half of <see cref="ParamsHash"/>: the parameters, without the
    /// source's track signature. Null on records written before this column existed.
    /// </summary>
    public string? ProfileParamsHash { get; set; }
    public string SidecarPath { get; set; } = string.Empty;
    public List<string> ProcessedLanguages { get; set; } = new();
    /// <summary>
    /// A run looked at this item for this profile and found nothing to encode. Its own status
    /// rather than a <c>Skipped</c> with a reason string: the forecast has to tell it from work
    /// outstanding, and matching on free text is not a thing to build a number on.
    /// </summary>
    public const string NothingToProcess = "NothingToProcess";

    public string Status { get; set; } = "Success"; // Success | Failed | Skipped | NothingToProcess
    public string? SkipReason { get; set; }
    public long ProcessedAtUtc { get; set; }
    public bool ExemptFromCleanup { get; set; } = false;
}
