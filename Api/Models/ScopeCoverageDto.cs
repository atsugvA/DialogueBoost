namespace Jellyfin.Plugin.DialogueBoost.Api.Models;

/// <summary>
/// One scope, against the library as it is now.
/// </summary>
public class ScopeCoverageDto : SelectionScopeDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the row still resolves to anything. A scope whose
    /// folders were all deleted is reported rather than quietly dropped, so the page can show why
    /// the number of selected rows changed.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets the row's display name, or <c>null</c> if it no longer resolves.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the row's item kind — <c>CollectionFolder</c>, <c>Series</c>, <c>Movie</c> — or
    /// <c>null</c> if it no longer resolves.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets how many entities the row stands for. More than one is a show Jellyfin split
    /// across release folders.
    /// </summary>
    public int MemberCount { get; set; }

    /// <summary>
    /// Gets or sets the number of processable videos under this scope.
    /// </summary>
    public int ItemCount { get; set; }
}
