using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Processing;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// Turns the stored scopes into the items a run should look at, from the library as it is now.
/// </summary>
/// <remarks>
/// This is the other half of "a selection is a scope, not a snapshot": nothing about a scope's
/// membership is stored, so a file that landed after the scope was chosen is covered without anyone
/// touching the configuration.
///
/// Members come from a library query rather than from <c>Folder.GetRecursiveChildren</c>, which
/// walks the in-memory child tree and therefore returns **nothing** for a season that has no folder
/// of its own — a real gap here, where seasons are among the stored scopes.
///
/// A scope names a row of <see cref="LibraryTree"/>, which may stand for several entities at once —
/// nine release folders of one show, eight season entities behind one <c>Season 2</c> row. Resolving
/// through the tree is what makes a row cover exactly what it displays.
/// </remarks>
public sealed class ScopeResolver
{
    private readonly LibraryTree _tree;
    private readonly IMediaLibrary _library;
    private readonly ILogger<ScopeResolver> _logger;

    public ScopeResolver(LibraryTree tree, IMediaLibrary library, ILogger<ScopeResolver> logger)
    {
        _tree = tree;
        _library = library;
        _logger = logger;
    }

    /// <summary>
    /// Resolves every scope separately, keeping the association — what the page shows, and what a
    /// per-scope watermark needs.
    /// </summary>
    public IReadOnlyList<ResolvedScope> ResolveScopes(LibrarySelection selection)
    {
        var resolved = new List<ResolvedScope>(selection.Scopes.Count);

        foreach (var scope in selection.Scopes)
        {
            var nodes = _tree.Resolve(scope.Path);
            if (nodes.Count == 0)
            {
                _logger.LogDebug(
                    "DialogueBoost: the selected row {Path} is no longer in the library.",
                    scope.Path);
                resolved.Add(new ResolvedScope(scope, nodes, Array.Empty<BaseItem>()));
                continue;
            }

            // One row, several entities: the nine folders a show was split across are one scope, and
            // an item under two of them is still one piece of work.
            var items = nodes
                .SelectMany(node => Members(node, scope, selection.CoverNewMedia))
                .DistinctBy(item => item.Id)
                .ToList();

            resolved.Add(new ResolvedScope(scope, nodes, items));
        }

        return resolved;
    }

    /// <summary>
    /// Resolves the whole selection to the distinct items a run should process. Scopes may overlap —
    /// a library and a series inside it — and an item covered twice is still one piece of work.
    /// </summary>
    public IReadOnlyList<BaseItem> ResolveItems(LibrarySelection selection)
    {
        var scopes = ResolveScopes(selection);
        var items = scopes
            .SelectMany(s => s.Items)
            .DistinctBy(i => i.Id)
            .ToList();

        int missing = scopes.Count(s => s.Nodes.Count == 0);
        if (missing > 0)
        {
            _logger.LogWarning(
                "DialogueBoost: {Missing} of {Total} selected row(s) are no longer in the library and were skipped.",
                missing,
                scopes.Count);
        }

        // {Frozen:l} literally: Jellyfin's file sink renders string properties quoted, so the
        // usual case — nothing to add — printed "cover 284 item(s)"".", two quote marks standing
        // for the empty half of the sentence.
        _logger.LogInformation(
            "DialogueBoost: {Scopes} selected scope(s) cover {Items} item(s){Frozen:l}.",
            scopes.Count,
            items.Count,
            selection.CoverNewMedia ? string.Empty : ", frozen to what they held when they were chosen");

        return items;
    }

    /// <summary>
    /// The processable videos under one node.
    /// </summary>
    private IReadOnlyList<BaseItem> Members(BaseItem node, SelectionScope scope, bool coverNewMedia)
    {
        if (node is not Folder folder)
        {
            // A single video chosen on its own: it is the whole scope, and nothing appears "inside"
            // it later, so the watermark does not apply.
            return ProcessableItem.Is(node) ? new[] { node } : Array.Empty<BaseItem>();
        }

        IEnumerable<BaseItem> members = _library
            .Within(folder, ProcessableItem.Kinds)
            .Where(ProcessableItem.Is);

        if (!coverNewMedia)
        {
            // No MaxDateCreated exists on the query, so the freeze is applied here. DateCreated is
            // when the file landed, not when it was scanned.
            members = members.Where(item => item.DateCreated.ToUniversalTime() <= scope.AddedAtUtc);
        }

        return members.ToList();
    }
}
