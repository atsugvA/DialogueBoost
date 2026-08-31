using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// What one stored scope covers, as of now.
/// </summary>
/// <param name="Scope">The stored scope.</param>
/// <param name="Nodes">
/// The library entities the path names — nine of them for a show Jellyfin split across release
/// folders. Empty means the row is no longer in the library, which is reported rather than dropped
/// so a shrinking selection has a visible reason.
/// </param>
/// <param name="Items">The processable videos the scope covers at this moment.</param>
public sealed record ResolvedScope(
    SelectionScope Scope,
    IReadOnlyList<BaseItem> Nodes,
    IReadOnlyList<BaseItem> Items);
