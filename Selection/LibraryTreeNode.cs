using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// One row of the tree the config page draws.
/// </summary>
/// <param name="Path">Where the row sits, and what is stored if the user picks it.</param>
/// <param name="Name">What the row is called.</param>
/// <param name="Kind">The Jellyfin item kind behind the row — <c>Series</c>, <c>Season</c>, …</param>
/// <param name="MemberIds">
/// The entities the row stands for. More than one means a show Jellyfin split across release
/// folders; the row covers all of them, which is the whole point of grouping.
/// </param>
/// <param name="CanExpand">Whether anything is inside it.</param>
/// <param name="Number">
/// Which one of its siblings this is — <c>E3</c> — or <c>null</c> when nothing numbers the row. Kept
/// beside the name rather than folded into it, so that searching and grouping keep matching on the
/// name the library actually holds (<see cref="RowNumber"/>).
/// </param>
public sealed record LibraryTreeNode(
    ScopePath Path,
    string Name,
    string Kind,
    IReadOnlyList<Guid> MemberIds,
    bool CanExpand,
    string? Number = null);
