using System;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Api.Models;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DialogueBoost.Api;

/// <summary>
/// Serves the tree the config page draws.
/// </summary>
/// <remarks>
/// The page used to build it from Jellyfin's <c>/Items</c>. Two things were wrong with that, and one
/// change answers both: <c>/Items</c> answers with Jellyfin's *merged* view, so a row showed more
/// than the scope behind it covered, and it made the plugin's UI depend on the query semantics
/// of a surface it does not own, which is exactly what the Integration seam exists to prevent. Rows now
/// come from <see cref="LibraryTree"/>, which is the same relation a run resolves.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class LibraryController : ControllerBase
{
    private readonly LibraryTree _tree;

    public LibraryController(LibraryTree tree)
    {
        _tree = tree;
    }

    /// <summary>
    /// Gets the rows one level under a path.
    /// </summary>
    /// <param name="path">
    /// The row to open, as returned by a previous call. Omit it for the top level, which is the
    /// libraries.
    /// </param>
    [HttpGet("/Plugins/DialogueBoost/Library/Children")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibraryChildrenDto> GetChildren([FromQuery] string? path)
    {
        var scope = ScopePath.Parse(path);
        return Ok(new LibraryChildrenDto
        {
            Path = scope.ToString(),
            Nodes = _tree.Children(scope).Select(Describe).ToList()
        });
    }

    /// <summary>
    /// Gets the individual entities behind one row, named by the folders that tell them apart.
    /// </summary>
    /// <param name="path">The row to look inside.</param>
    [HttpGet("/Plugins/DialogueBoost/Library/Members")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibraryChildrenDto> GetMembers([FromQuery] string? path)
    {
        var scope = ScopePath.Parse(path);

        return Ok(new LibraryChildrenDto
        {
            Path = scope.ToString(),
            Nodes = _tree.Members(scope).Select(Describe).ToList()
        });
    }

    /// <summary>
    /// Gets the rows whose name contains <paramref name="q"/>, wherever in the tree they sit.
    /// </summary>
    /// <remarks>
    /// Served, not filtered in the browser: the page holds one level at a time, so a client-side
    /// filter could only search what someone has already opened.
    /// </remarks>
    /// <param name="q">What to look for, case-insensitively, anywhere in a row's name.</param>
    /// <param name="limit">How many rows to answer with at most.</param>
    [HttpGet("/Plugins/DialogueBoost/Library/Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibrarySearchDto> GetSearch([FromQuery] string? q, [FromQuery] int limit = 50)
    {
        var hits = _tree.Search(q ?? string.Empty, Math.Clamp(limit, 1, 200), out bool complete);

        return Ok(new LibrarySearchDto
        {
            Query = q ?? string.Empty,
            Complete = complete,
            Nodes = hits
                .Select(hit => Describe<LibrarySearchNodeDto>(hit.Row, found => found.Location = hit.Location))
                .ToList()
        });
    }

    private static LibraryNodeDto Describe(LibraryTreeNode node) => Describe<LibraryNodeDto>(node);

    private static T Describe<T>(LibraryTreeNode node, Action<T>? also = null)
        where T : LibraryNodeDto, new()
    {
        var described = new T
        {
            Path = node.Path.ToString(),
            Name = node.Name,
            Kind = node.Kind,
            Number = node.Number,
            MemberCount = node.MemberIds.Count,
            CanExpand = node.CanExpand
        };

        also?.Invoke(described);
        return described;
    }
}
