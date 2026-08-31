using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Processing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.DialogueBoost.Selection;

/// <summary>
/// The library as the config page draws it: libraries at the top, grouped children below, every row
/// carrying the path that names it.
/// </summary>
/// <remarks>
/// The page used to build its tree from Jellyfin's
/// <c>/Items</c>, which answers a <c>ParentId</c> query with its *merged presentation* view — so one
/// <c>Season 2</c> row listed eight episodes owned by eight different season entities, while a scope
/// resolved physically and covered one of them. A row that shows eight things and covers one
/// is the second half of the reported "selection marks lie", and it cannot be fixed in the browser:
/// only the plugin can make what a row shows and what a row covers the same query.
///
/// So rows come from the same relation <see cref="ScopeResolver"/> uses — direct children by
/// library query, never <c>Folder.Children</c>, which is empty for a season Jellyfin synthesised
/// from episode filenames — and the duplicates that leaves behind are grouped here rather than
/// merged by Jellyfin. One consequence worth stating: the page now depends on this file's shape and
/// not on <c>/Items</c> query semantics, so a change there lands in one place.
/// </remarks>
public sealed class LibraryTree
{
    /// <summary>
    /// What a row may be: something to descend into, or something to process. Anything else — a
    /// season's trailers, an audio track, a photo — is not a row.
    /// </summary>
    private static readonly BaseItemKind[] TreeKinds =
        new[] { BaseItemKind.Folder, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.BoxSet }
            .Concat(ProcessableItem.Kinds)
            .ToArray();

    /// <summary>
    /// How many rows one search may look at. Reached only by a search that matches little in a large
    /// library; the whole of this project's own library is 376 rows.
    /// </summary>
    private const int SearchBudget = 20000;

    private readonly IMediaLibrary _library;
    private readonly ItemContainers _containers;

    public LibraryTree(IMediaLibrary library, ItemContainers containers)
    {
        _library = library;
        _containers = containers;
    }

    /// <summary>
    /// The libraries, which are the tree's top level and the only nodes named by a plain id: a
    /// library is unique, so there is nothing to group it with.
    /// </summary>
    public IReadOnlyList<BaseItem> Libraries() => _library.Libraries();

    /// <summary>
    /// The entities a path names — one for a row that turned out to be unique, nine for a show split
    /// across release folders, none for a row that has since gone.
    /// </summary>
    public IReadOnlyList<BaseItem> Resolve(ScopePath path)
    {
        if (path.IsRoot)
        {
            return Libraries();
        }

        var nodes = Anchor(path.Segments[0]);

        for (int level = 1; level < path.Segments.Count && nodes.Count > 0; level++)
        {
            string segment = path.Segments[level];

            // An id naming one of the nodes already in hand narrows to it rather than descending:
            // that is a single release folder picked out of the group its show was split into, which
            // sits beside the other eight and not underneath them. Keeping it a segment of the
            // group's own path is what makes choosing the group also cover the folder rows it opens
            // to, by the same prefix test as everything else.
            var id = ScopeSegment.ItemId(segment);
            nodes = id != Guid.Empty && nodes.Any(node => node.Id == id)
                ? nodes.Where(node => node.Id == id).ToList()
                : DirectChildren(nodes).Where(candidate => ScopeSegment.Matches(segment, candidate)).ToList();
        }

        return nodes;
    }

    /// <summary>
    /// Where a path starts. Normally a library, and looked up by id rather than searched for among
    /// the libraries so that a selection stored before the plugin had a tree — a bare item id,
    /// anchored at whatever the user clicked — still names exactly what it always named.
    /// </summary>
    private IReadOnlyList<BaseItem> Anchor(string segment)
    {
        var id = ScopeSegment.ItemId(segment);
        if (id != Guid.Empty)
        {
            return _library.ById(id) is BaseItem item
                ? new[] { item }
                : Array.Empty<BaseItem>();
        }

        return Libraries().Where(library => ScopeSegment.Matches(segment, library)).ToList();
    }

    /// <summary>
    /// Where an item sits in this tree, or <c>null</c> if no row leads to it.
    /// </summary>
    /// <remarks>
    /// What a selection stored as a bare item id becomes once there is a tree to place it in.
    ///
    /// It descends rather than walking up, because up does not reach the top: a library is a
    /// <c>CollectionFolder</c>, and appears in no item's parent chain. The ancestor set is used
    /// only to choose which row to descend into, so the walk stays bounded by the depth of the tree
    /// rather than its width.
    ///
    /// It answers with *a* row, not with every row that leads to the item, so it cannot be used to
    /// decide coverage — for that, resolve the scopes and see whether the item is among what they
    /// cover, which is the question a run asks anyway.
    /// </remarks>
    public ScopePath? PathOf(BaseItem item)
    {
        var ancestors = _containers.Of(item).ToHashSet();
        ancestors.Add(item.Id);

        foreach (var library in Libraries().Where(library => ancestors.Contains(library.Id)))
        {
            var level = new Level(
                new LibraryTreeNode(
                    ScopePath.Root.Append(ScopeSegment.ForItem(library.Id)),
                    library.Name,
                    library.GetBaseItemKind().ToString(),
                    new[] { library.Id },
                    CanExpand: true),
                new[] { library });

            while (level is not null)
            {
                if (level.Row.MemberIds.Contains(item.Id))
                {
                    return level.Row.Path;
                }

                level = Rows(level).FirstOrDefault(row => row.Row.MemberIds.Any(ancestors.Contains));
            }
        }

        return null;
    }

    /// <summary>
    /// The rows one level under a path. The root's children are the libraries.
    /// </summary>
    public IReadOnlyList<LibraryTreeNode> Children(ScopePath path)
    {
        if (path.IsRoot)
        {
            return Libraries()
                .Select(library => Root(library).Row)
                .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        return Group(path, DirectChildren(Resolve(path))).Select(level => level.Row).ToList();
    }

    /// <summary>
    /// One row, with the entities behind it kept.
    /// </summary>
    /// <remarks>
    /// A walk that carried only paths would resolve each one from the library down again at every
    /// step, which is the same queries over and over: searching this project's own 376-row library
    /// cost 1.1 seconds that way, and 0.24 with the entities in hand.
    /// </remarks>
    private sealed record Level(LibraryTreeNode Row, IReadOnlyList<BaseItem> Nodes);

    /// <summary>
    /// A library, as the top level of the tree: the only row named by a plain id, because a library
    /// is unique and there is nothing to group it with.
    /// </summary>
    private static Level Root(BaseItem library) => new(
        new LibraryTreeNode(
            ScopePath.Root.Append(ScopeSegment.ForItem(library.Id)),
            library.Name,
            library.GetBaseItemKind().ToString(),
            new[] { library.Id },
            CanExpand: true),
        new[] { library });

    /// <summary>
    /// The rows one level under a row whose entities are already known.
    /// </summary>
    private IReadOnlyList<Level> Rows(Level level) =>
        Group(level.Row.Path, DirectChildren(level.Nodes));

    /// <summary>
    /// The rows whose name contains <paramref name="text"/>, nearest the top first.
    /// </summary>
    /// <remarks>
    /// Served rather than filtered in the browser, because the page holds one level at a time: a
    /// client-side filter can only search what someone has already opened, which is the opposite of
    /// what a search is for.
    ///
    /// It walks the same <see cref="Children"/> rows the tree draws — breadth first, so a show is
    /// found before its episodes — rather than asking the library for items by name and working back
    /// to the rows they sit in. One relation, so a search cannot offer a row the tree does not have,
    /// and a hit is selectable exactly as if it had been found by opening folders.
    ///
    /// The walk is bounded twice: it stops at <paramref name="limit"/> hits, and after
    /// <see cref="SearchBudget"/> rows have been looked at. A caller is told which happened rather
    /// than being handed a short list that looks complete.
    /// </remarks>
    /// <param name="text">What to look for, case-insensitively, anywhere in a row's name.</param>
    /// <param name="limit">How many hits to return at most.</param>
    /// <param name="complete">Whether the whole tree was looked at.</param>
    public IReadOnlyList<LibrarySearchHit> Search(string text, int limit, out bool complete)
    {
        var hits = new List<LibrarySearchHit>();
        complete = true;

        string wanted = text.Trim();
        if (wanted.Length == 0 || limit <= 0)
        {
            return hits;
        }

        var pending = new Queue<(Level Level, string Location)>();
        foreach (var library in Libraries())
        {
            pending.Enqueue((Root(library), string.Empty));
        }

        int examined = 0;

        while (pending.Count > 0)
        {
            var (level, location) = pending.Dequeue();
            string below = location.Length == 0 ? level.Row.Name : location + " › " + level.Row.Name;

            foreach (var found in Rows(level))
            {
                if (examined++ >= SearchBudget)
                {
                    complete = false;
                    return hits;
                }

                if (found.Row.Name.Contains(wanted, StringComparison.CurrentCultureIgnoreCase))
                {
                    hits.Add(new LibrarySearchHit(found.Row, below));
                    if (hits.Count >= limit)
                    {
                        complete = false;
                        return hits;
                    }
                }

                if (found.Row.CanExpand)
                {
                    pending.Enqueue((found, below));
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// The individual entities behind a row, named by the folder that distinguishes them. This is
    /// what turns "Tin Sparrow · 9 folders" into something a user can look inside and disagree with —
    /// and it is how a single release folder gets picked on its own.
    /// </summary>
    public IReadOnlyList<LibraryTreeNode> Members(ScopePath path) =>
        Resolve(path)
            .OrderBy(RowNumber.Position)
            .ThenBy(FolderName, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new LibraryTreeNode(
                path.Append(ScopeSegment.ForItem(item.Id)),
                FolderName(item),
                item.GetBaseItemKind().ToString(),
                new[] { item.Id },
                item is Folder,
                RowNumber.Of(item)))
            .ToList();

    /// <summary>
    /// Collapses same-named siblings into one row apiece, keeping the entities behind each.
    /// </summary>
    /// <remarks>
    /// Ordered by number first and by name only after it, because a season's episodes are a
    /// sequence: sorted by name they read <c>All the Sparrows, Anchor and Chain, Midnight Signal</c>
    /// when they happen in the order 7, 5, 2, and where nothing titled them the name is the filename
    /// and says even less (<see cref="RowNumber"/>). A group's number is its first member's — the
    /// members of a group are the same thing arriving more than once, so any of them answers.
    /// </remarks>
    private static IReadOnlyList<Level> Group(ScopePath path, IReadOnlyList<BaseItem> children) =>
        children
            .GroupBy(ScopeSegment.ForGroup, StringComparer.Ordinal)
            .OrderBy(group => RowNumber.Position(group.First()))
            .ThenBy(group => group.First().Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new Level(
                new LibraryTreeNode(
                    path.Append(group.Key),
                    group.First().Name,
                    group.First().GetBaseItemKind().ToString(),
                    group.Select(item => item.Id).ToList(),
                    group.Any(item => item is Folder),
                    RowNumber.Of(group.First())),
                group.ToList()))
            .ToList();

    /// <summary>
    /// The rows one level under a set of nodes, de-duplicated.
    /// </summary>
    private IReadOnlyList<BaseItem> DirectChildren(IReadOnlyList<BaseItem> nodes)
    {
        var children = new List<BaseItem>();
        var seen = new HashSet<Guid>();

        foreach (var child in nodes.SelectMany(ChildrenOf))
        {
            if (seen.Add(child.Id))
            {
                children.Add(child);
            }
        }

        return children;
    }

    /// <summary>
    /// The rows one level under a single node.
    /// </summary>
    /// <remarks>
    /// Three cases, each measured on the live 10.11.11 rather than reasoned about, because
    /// Jellyfin's idea of "inside" is not one relation:
    ///
    /// * **A library** is a <c>CollectionFolder</c> — a node in Jellyfin's virtual tree, and nobody's
    ///   parent. Its rows belong to the folders on disk behind it.
    /// * **A season** usually has no folder of its own: it is synthesised from episode filenames, and
    ///   the episodes' parent stays the release folder they physically sit in. Asking for its
    ///   children by parent returns nothing, so a season is asked recursively — which for a season is
    ///   the same one level, since only episodes are under it.
    /// * **Everything else** is a plain parent lookup. The multi-parent helper
    ///   <c>GetItemList(query, parents)</c> cannot be used for this: it rewrites the parents into
    ///   <c>AncestorIds</c>/<c>TopParentIds</c>, a containment filter, so <c>Recursive = false</c>
    ///   stops restricting anything — asked for the children of <c>Shows</c> it answered with 352
    ///   rows spanning every series, season and episode in it, instead of the 25 shows one level down.
    /// </remarks>
    private IEnumerable<BaseItem> ChildrenOf(BaseItem node)
    {
        if (node is Season season)
        {
            return Query(season.Id, recursive: true);
        }

        if (node is CollectionFolder library)
        {
            return library.PhysicalFolderIds.SelectMany(id => Query(id, recursive: false));
        }

        return node is Folder ? WithoutWhatTheSeasonRowsShow(node, Query(node.Id, recursive: false)) : Array.Empty<BaseItem>();
    }

    /// <summary>
    /// Drops the children that a season listed beside them already shows.
    /// </summary>
    /// <remarks>
    /// A series whose episodes each arrived in their own release folder has two kinds of child at
    /// once: the <c>Season</c> Jellyfin synthesised from the filenames, and the release folders the
    /// episodes physically sit in. They are parallel containers of the same episodes — the season
    /// reaches them by containment, the folder by parentage — so listing both shows every episode
    /// twice, at two depths, under two paths. That is the reported "multiple parents", and a
    /// row that lists a thing twice cannot honestly say what it covers.
    ///
    /// Jellyfin's own answer to the same question is the season alone: <c>/Items?ParentId=</c> a
    /// series whose six episodes came in six folders returns <c>Season 1</c> and nothing else.
    ///
    /// So the seasons keep their episodes, and a folder is dropped only once the seasons are shown
    /// to reach everything inside it — never on the assumption that they do. A folder holding
    /// something no season accounts for stays, because it is then the only row that leads there.
    /// </remarks>
    private IReadOnlyList<BaseItem> WithoutWhatTheSeasonRowsShow(BaseItem parent, IReadOnlyList<BaseItem> children)
    {
        var seasons = children.OfType<Season>().Select(season => season.Id).ToHashSet();
        if (seasons.Count == 0)
        {
            return children;
        }

        // Everything under here that no season row reaches, and every container on the way down to
        // it: those are the folders that still have to be rows. Only asked when there is a folder to
        // decide about.
        var needed = children.Any(IsPlainFolder)
            ? Query(parent.Id, recursive: true)
                .Where(item => ProcessableItem.Is(item)
                               && !(item is Episode episode && seasons.Contains(episode.SeasonId)))
                .SelectMany(item => _containers.Of(item))
                .ToHashSet()
            : new HashSet<Guid>();

        return children.Where(child => child switch
        {
            // An episode belonging to no season listed here stays a row of its own.
            Episode episode => !seasons.Contains(episode.SeasonId),

            _ => !IsPlainFolder(child) || needed.Contains(child.Id)
        }).ToList();
    }

    /// <summary>
    /// A folder on disk, as opposed to a container Jellyfin built to present it — a series, a
    /// season, a collection.
    /// </summary>
    private static bool IsPlainFolder(BaseItem item) => item is Folder and not (Season or Series or BoxSet);

    private IReadOnlyList<BaseItem> Query(Guid parentId, bool recursive) =>
        _library.Under(parentId, TreeKinds, recursive);

    /// <summary>
    /// What tells one member of a group from another: the folder it came in, which is the only thing
    /// about the nine <c>Tin Sparrow</c> entities that differs.
    /// </summary>
    private static string FolderName(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return item.Name;
        }

        string name = item.IsFolder
            ? Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(item.Path);

        return string.IsNullOrEmpty(name) ? item.Name : name;
    }
}
