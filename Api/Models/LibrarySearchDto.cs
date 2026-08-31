using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// What a search over the tree found.
/// </summary>
public class LibrarySearchDto
{
    /// <summary>
    /// Gets or sets what was searched for, echoed so a client can drop an answer that arrived after
    /// the user had typed on.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the matching rows, the ones nearest the top of the tree first.
    /// </summary>
    public IReadOnlyList<LibrarySearchNodeDto> Nodes { get; set; } = Array.Empty<LibrarySearchNodeDto>();

    /// <summary>
    /// Gets or sets a value indicating whether the whole tree was looked at. <c>false</c> means the
    /// search stopped early — there may be more matches — and the page has to say so rather than
    /// presenting a partial list as the answer.
    /// </summary>
    public bool Complete { get; set; }
}
