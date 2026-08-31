using System;

namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// One stored scope, exactly as it is stored.
/// </summary>
/// <remarks>
/// What the row currently resolves to — its name, whether it is still there, how many entities it
/// stands for — is <see cref="ScopeCoverageDto"/>'s answer, because working it out costs a query per
/// scope and reading the stored selection should not.
/// </remarks>
public class SelectionScopeDto
{
    /// <summary>
    /// Gets or sets the chosen row, as a tree path. This is the identity — the row can stand for one
    /// entity or for the nine release folders a show was split across.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the row was chosen.
    /// </summary>
    public DateTime AddedAtUtc { get; set; }
}
