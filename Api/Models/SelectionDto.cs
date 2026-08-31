using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// The stored selection, as served to a client.
/// </summary>
public class SelectionDto
{
    /// <summary>
    /// Gets or sets the rows the user chose. Only those — what is under them is resolved per run,
    /// never stored.
    /// </summary>
    public IReadOnlyList<SelectionScopeDto> Scopes { get; set; } = Array.Empty<SelectionScopeDto>();

    /// <summary>
    /// Gets or sets a value indicating whether a scope keeps covering media that appears inside it
    /// after it was chosen.
    /// </summary>
    public bool CoverNewMedia { get; set; }

    /// <summary>
    /// Gets or sets when the selection was last written, or <c>null</c> if it never has been.
    /// </summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the libraries added to Jellyfin since the selection was last saved and not part
    /// of it, as tree paths. Nothing contains a library, so no scope can cover one — these are the
    /// rows the user has never had the chance to decide about.
    /// </summary>
    public IReadOnlyList<string> NewLibraryPaths { get; set; } = Array.Empty<string>();
}
