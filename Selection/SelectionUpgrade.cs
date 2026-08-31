using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// Places a selection stored as item ids into the tree the plugin now serves.
/// </summary>
/// <remarks>
/// Before the plugin served its own tree, a chosen row was stored as the id of the entity behind it.
/// That is still a valid scope — it covers exactly that entity — but it is not a row of the tree, so
/// nothing on the page marks it, and it carries forward both problems the tree was built to answer:
/// the nine ids a show was split across stay nine separate scopes, and a season stored as
/// one entity keeps covering one episode of the eight its row displays.
///
/// Ids that cannot be placed are left exactly as they are. An id naming something the tree cannot
/// reach is better left covering what it covers than rewritten into a guess.
/// </remarks>
public sealed class SelectionUpgrade
{
    private readonly SelectionStore _store;
    private readonly LibraryTree _tree;
    private readonly ILogger<SelectionUpgrade> _logger;

    public SelectionUpgrade(SelectionStore store, LibraryTree tree, ILogger<SelectionUpgrade> logger)
    {
        _store = store;
        _tree = tree;
        _logger = logger;
    }

    /// <summary>
    /// Rewrites what needs rewriting, and touches nothing when nothing does.
    /// </summary>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var selection = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (selection.IsEmpty)
        {
            return;
        }

        var libraries = _tree.Libraries().Select(library => library.Id).ToHashSet();
        if (libraries.Count == 0)
        {
            // Asked too early to answer honestly: placing scopes against a library that is not up
            // yet would find a row for none of them.
            _logger.LogDebug("DialogueBoost: no libraries are readable yet, so the stored selection is left alone.");
            return;
        }

        var loose = selection.Scopes.Where(scope => IsLooseId(scope, libraries)).ToList();
        if (loose.Count == 0)
        {
            return;
        }

        var placed = new Dictionary<ScopePath, SelectionScope>();
        int unplaceable = 0;

        foreach (var scope in selection.Scopes)
        {
            var path = (loose.Contains(scope) ? Place(scope.Path) : null) ?? scope.Path;
            if (loose.Contains(scope) && path.Equals(scope.Path))
            {
                unplaceable++;
            }

            // Several ids collapsing onto one row keep the oldest watermark: it freezes the least,
            // so a scope frozen by CoverNewMedia cannot silently widen here.
            if (!placed.TryGetValue(path, out var kept) || scope.AddedAtUtc < kept.AddedAtUtc)
            {
                placed[path] = scope with { Path = path };
            }
        }

        if (unplaceable == loose.Count)
        {
            // Every id that is still an id names something no row leads to. Storing what is already
            // stored, and announcing it, on every page visit would be the only effect.
            _logger.LogDebug(
                "DialogueBoost: {Count} selected item id(s) have no row in the library tree, and stay ids.",
                unplaceable);
            return;
        }

        await _store.RewriteAsync(placed.Values.ToList(), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "DialogueBoost: placed a selection stored as item ids into the library tree — {Before} scope(s) became {After}{Unplaceable:l}.",
            selection.Scopes.Count,
            placed.Count,
            unplaceable > 0 ? $"; {unplaceable} kept as ids because no row leads to them" : string.Empty);
    }

    /// <summary>
    /// Whether a scope is one of the old ones. A library is legitimately a single id — it is the
    /// tree's top level — so only a lone id that is *not* a library has a row to be found for it.
    /// </summary>
    private static bool IsLooseId(SelectionScope scope, IReadOnlySet<Guid> libraries) =>
        scope.Path.Segments.Count == 1
        && ScopeSegment.ItemId(scope.Path.Segments[0]) is var id
        && id != Guid.Empty
        && !libraries.Contains(id);

    private ScopePath? Place(ScopePath path) =>
        _tree.Resolve(path) is [BaseItem item, ..] ? _tree.PathOf(item) : null;
}
