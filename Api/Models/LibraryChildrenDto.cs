using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// One level of the plugin's library tree.
/// </summary>
public class LibraryChildrenDto
{
    /// <summary>
    /// Gets or sets the path whose children these are. Empty is the root, whose children are the
    /// libraries.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rows, ordered by name.
    /// </summary>
    public IReadOnlyList<LibraryNodeDto> Nodes { get; set; } = Array.Empty<LibraryNodeDto>();
}
