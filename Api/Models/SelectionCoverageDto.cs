using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// What the stored selection covers in the library right now.
/// </summary>
/// <remarks>
/// Resolved on request rather than stored: the whole point of a scope is that its membership is a
/// question with a different answer tomorrow.
/// </remarks>
public class SelectionCoverageDto
{
    /// <summary>
    /// Gets or sets each scope with the number of items it covers.
    /// </summary>
    public IReadOnlyList<ScopeCoverageDto> Scopes { get; set; } = Array.Empty<ScopeCoverageDto>();

    /// <summary>
    /// Gets or sets the number of distinct items a run would look at. Lower than the sum of the
    /// per-scope counts when scopes overlap — a library and a series inside it cover the same
    /// episodes, which is still one piece of work each.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether scopes keep covering media that appears later.
    /// </summary>
    public bool CoverNewMedia { get; set; }
}
