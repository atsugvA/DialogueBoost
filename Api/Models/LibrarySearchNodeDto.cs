namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// One row a search found: a row like any other, plus where it sits.
/// </summary>
public class LibrarySearchNodeDto : LibraryNodeDto
{
    /// <summary>
    /// Gets or sets the rows above it, named — <c>Shows › Tin Sparrow</c>. A path cannot be read back into
    /// names, so the walk down to the row reports them.
    /// </summary>
    public string Location { get; set; } = string.Empty;
}
