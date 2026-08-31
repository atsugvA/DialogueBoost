namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// One row of the plugin's library tree.
/// </summary>
public class LibraryNodeDto
{
    /// <summary>
    /// Gets or sets where the row sits in the tree — and exactly what is stored if the user picks
    /// it. A path rather than an id, so that a row standing for nine release folders keeps standing
    /// for them when a tenth arrives.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the row's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin item kind behind the row.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which one of its siblings the row is — <c>E3</c> — or <c>null</c> when nothing
    /// numbers it. An episode's name is its title, or its filename where nothing titled it; neither
    /// says which episode it is, so the page shows this beside the name.
    /// </summary>
    public string? Number { get; set; }

    /// <summary>
    /// Gets or sets how many entities the row stands for. Anything above one is a show Jellyfin
    /// split across release folders; the row covers all of them.
    /// </summary>
    public int MemberCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether there is anything inside the row.
    /// </summary>
    public bool CanExpand { get; set; }
}
