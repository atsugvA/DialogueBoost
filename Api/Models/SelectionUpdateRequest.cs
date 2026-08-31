using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// A request to replace the stored selection with exactly these rows.
/// </summary>
public class SelectionUpdateRequest
{
    /// <summary>
    /// Gets or sets the rows the user chose, as tree paths. Required; an empty array clears the
    /// selection. Duplicates collapse. Every path must resolve to something in the library, or the
    /// whole request is rejected — a partially applied selection is the failure this endpoint exists
    /// to prevent.
    /// </summary>
    public IReadOnlyList<string>? Paths { get; set; }

    /// <summary>
    /// Gets or sets whether scopes keep covering media that appears inside them later. Omit to
    /// leave the stored value alone.
    /// </summary>
    public bool? CoverNewMedia { get; set; }
}
