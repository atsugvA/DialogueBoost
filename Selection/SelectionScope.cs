using System;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// One row the user chose to cover: that row and everything under it, now and later.
/// </summary>
/// <remarks>
/// A scope is deliberately not a snapshot of what the row contained when it was picked. Members are
/// resolved from the library on every run, which is what makes a download that lands tomorrow
/// covered without anyone opening the config page — and what stops the saved list from exploding
/// into every descendant id.
///
/// It is a <see cref="ScopePath"/> and not an item id for the case containment cannot reach: a
/// library laid out as one release folder per episode makes Jellyfin build a separate series entity
/// per folder, so the tenth folder to arrive is a *sibling* of the nine already chosen, inside
/// nothing that was chosen. A path names the show, and keeps naming it.
/// </remarks>
/// <param name="Path">The chosen row.</param>
/// <param name="AddedAtUtc">
/// When the row was chosen. Preserved across re-saves, because it is the watermark that freezes a
/// scope's membership when <c>CoverNewMedia</c> is off.
/// </param>
public sealed record SelectionScope(ScopePath Path, DateTime AddedAtUtc);
