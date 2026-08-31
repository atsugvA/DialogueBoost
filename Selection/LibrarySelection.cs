using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// Everything the plugin knows about what the user asked it to cover.
/// </summary>
/// <remarks>
/// One document, read and written as a whole. <see cref="CoverNewMedia"/> lives here rather than in
/// the plugin configuration because it changes what the stored scopes *mean*: split across two
/// documents, the two halves could be read a moment apart and disagree about which items a run
/// covers.
/// </remarks>
/// <param name="Scopes">The nodes the user chose, in the order they were stored.</param>
/// <param name="CoverNewMedia">
/// Whether a scope keeps covering items that appear inside it after it was chosen. Off, each scope
/// stays frozen to the members it had at its <see cref="SelectionScope.AddedAtUtc"/>.
/// </param>
/// <param name="UpdatedAtUtc">
/// When the selection was last written, or <c>null</c> if it has never been written — which is also
/// how the plugin knows a selection from an older version still has to be imported.
/// </param>
public sealed record LibrarySelection(
    IReadOnlyList<SelectionScope> Scopes,
    bool CoverNewMedia,
    DateTime? UpdatedAtUtc)
{
    /// <summary>
    /// The default for <see cref="CoverNewMedia"/>: on, so that picking libraries is the last time
    /// the user has to think about it.
    /// </summary>
    public const bool CoverNewMediaDefault = true;

    /// <summary>
    /// Gets the selection of a plugin that has never had one stored.
    /// </summary>
    public static LibrarySelection Empty { get; } =
        new(Array.Empty<SelectionScope>(), CoverNewMediaDefault, null);

    /// <summary>
    /// Gets a value indicating whether nothing is selected, so a run has nothing to do.
    /// </summary>
    public bool IsEmpty => Scopes.Count == 0;
}
