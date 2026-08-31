using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// Libraries that were added to Jellyfin after the user last saved a selection, and what the
/// configured policy says to do about them.
/// </summary>
/// <remarks>
/// New media inside a chosen node is covered by resolving the scope again. A new *library* is
/// the one case containment cannot answer: nothing contains it, so no scope reaches it however the
/// scopes are resolved — somebody has to decide.
///
/// "New" is derived, never stored: a library counts as new while it was created after the selection
/// was last written. Saving the selection is therefore also the act of having seen it, which is why
/// no list of dismissed libraries has to be kept anywhere (remembering a list and
/// diffing it is the thing this design avoids).
/// </remarks>
public sealed class NewLibraries
{
    private readonly IMediaLibrary _library;
    private readonly SelectionStore _store;
    private readonly ILogger<NewLibraries> _logger;

    public NewLibraries(IMediaLibrary library, SelectionStore store, ILogger<NewLibraries> logger)
    {
        _library = library;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Whether a library the user has not chosen counts as new.
    /// </summary>
    /// <param name="createdUtc">When Jellyfin created the library.</param>
    /// <param name="selectionSavedUtc">
    /// When the selection was last saved, or <c>null</c> if it never has been — in which case
    /// nothing is new, because the user has not yet said what they want at all.
    /// </param>
    public static bool IsNew(DateTime createdUtc, DateTime? selectionSavedUtc) =>
        selectionSavedUtc is DateTime saved && createdUtc.ToUniversalTime() > saved;

    /// <summary>
    /// The libraries that appeared since the selection was saved and are not in it. These are the
    /// rows the config page badges.
    /// </summary>
    public IReadOnlyList<BaseItem> Since(LibrarySelection selection)
    {
        // The same list the page draws its top-level rows from, so a badge always lands on a row
        // that exists.
        return _library.Libraries()
            .Where(item => !IsChosen(selection, item.Id) && IsNew(item.DateCreated, selection.UpdatedAtUtc))
            .ToList();
    }

    /// <summary>
    /// Whether the user has already decided about a library — by choosing it, or by choosing
    /// anything inside it, which they could only have done by looking at it.
    /// </summary>
    private static bool IsChosen(LibrarySelection selection, Guid libraryId)
    {
        var path = ScopePath.Root.Append(ScopeSegment.ForItem(libraryId));
        return selection.Scopes.Any(scope => path.Contains(scope.Path));
    }

    /// <summary>
    /// Applies the policy to a selection about to be used, and returns the selection to use —
    /// unchanged unless <see cref="NewLibraryPolicy.AutoInclude"/> had something to add.
    /// </summary>
    public async Task<LibrarySelection> ApplyPolicyAsync(
        LibrarySelection selection,
        NewLibraryPolicy policy,
        CancellationToken cancellationToken)
    {
        var arrived = Since(selection);
        if (arrived.Count == 0)
        {
            return selection;
        }

        if (policy != NewLibraryPolicy.AutoInclude)
        {
            _logger.LogInformation(
                "DialogueBoost: {Count} librar{Y:l} added since the selection was saved and not covered: {Names}. " +
                "Include them on the config page, or set new libraries to be covered automatically.",
                arrived.Count,
                arrived.Count == 1 ? "y was" : "ies were",
                string.Join(", ", arrived.Select(a => a.Name)));

            return selection;
        }

        var wanted = selection.Scopes.Select(s => s.Path)
            .Concat(arrived.Select(a => ScopePath.Root.Append(ScopeSegment.ForItem(a.Id))))
            .ToList();
        var stored = await _store.ReplaceAsync(wanted, coverNewMedia: null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "DialogueBoost: covering {Count} newly added librar{Y:l} — {Names}.",
            arrived.Count,
            arrived.Count == 1 ? "y" : "ies",
            string.Join(", ", arrived.Select(a => a.Name)));

        return stored;
    }
}
