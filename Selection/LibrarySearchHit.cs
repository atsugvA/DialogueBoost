namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// One row a search found, and where it sits.
/// </summary>
/// <param name="Row">The row itself — the same thing the tree draws, so it can be picked here.</param>
/// <param name="Location">
/// The rows above it, named: <c>Shows › Tin Sparrow</c>. Built while walking down to it, because a path
/// cannot be read back into names — a group segment carries the name lower-cased, which is what
/// makes a row keep its identity when someone re-capitalises a folder.
/// </param>
public sealed record LibrarySearchHit(LibraryTreeNode Row, string Location);
