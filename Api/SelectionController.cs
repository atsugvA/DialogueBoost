using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Api.Models;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Api;

/// <summary>
/// Reads and replaces the set of library nodes the plugin covers.
/// </summary>
/// <remarks>
/// The selection has its own endpoint, and its own document behind it, because saving it used to
/// mean rewriting the plugin's entire configuration — a read-modify-write with three unsynchronised
/// writers, where an overlap lost one of them silently. Here a save is one typed request that
/// either applies whole or is rejected whole, and always answers with what is now stored rather
/// than with the caller's own input.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class SelectionController : ControllerBase
{
    /// <summary>
    /// An arbitrary but finite ceiling: a selection is a handful of nodes, and anything near this
    /// means the client is storing descendants again rather than scopes.
    /// </summary>
    private const int MaxScopes = 5000;

    private readonly SelectionStore _store;
    private readonly ScopeResolver _resolver;
    private readonly LibraryTree _tree;
    private readonly SelectionUpgrade _upgrade;
    private readonly NewLibraries _newLibraries;
    private readonly ILogger<SelectionController> _logger;

    public SelectionController(
        SelectionStore store,
        ScopeResolver resolver,
        LibraryTree tree,
        SelectionUpgrade upgrade,
        NewLibraries newLibraries,
        ILogger<SelectionController> logger)
    {
        _store = store;
        _resolver = resolver;
        _tree = tree;
        _upgrade = upgrade;
        _newLibraries = newLibraries;
        _logger = logger;
    }

    /// <summary>
    /// Gets the stored selection: the chosen rows, and the libraries nobody has decided about yet.
    /// </summary>
    /// <remarks>
    /// This is also where a selection stored as bare item ids gets placed in the tree. A page visit
    /// is the one moment the library is certainly up, which the plugin's own start is not — and the
    /// page is about to draw exactly the rows the placement decides.
    /// </remarks>
    [HttpGet("/Plugins/DialogueBoost/Selection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SelectionDto>> GetSelection(CancellationToken cancellationToken)
    {
        await _upgrade.ApplyAsync(cancellationToken).ConfigureAwait(false);

        var selection = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        return Ok(Describe(selection));
    }

    /// <summary>
    /// Replaces the selection with exactly the nodes given.
    /// </summary>
    [HttpPut("/Plugins/DialogueBoost/Selection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SelectionDto>> PutSelection(
        [FromBody] SelectionUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (request?.Paths is null)
        {
            return BadRequest(new { Message = "Paths is required. Send an empty array to select nothing." });
        }

        var paths = request.Paths
            .Select(ScopePath.Parse)
            .Where(path => !path.IsRoot)
            .Distinct()
            .ToList();

        if (paths.Count > MaxScopes)
        {
            return BadRequest(new
            {
                Message = $"A selection of {paths.Count} rows exceeds the limit of {MaxScopes}. Select containers, not their contents — what is inside them is covered automatically."
            });
        }

        var unknown = paths.Where(path => _tree.Resolve(path).Count == 0).ToList();
        if (unknown.Count > 0)
        {
            _logger.LogWarning(
                "DialogueBoost: rejected a selection naming {Count} row(s) that are not in the library.",
                unknown.Count);

            return BadRequest(new
            {
                Message = $"{unknown.Count} of {paths.Count} selected row(s) are not in the library. Nothing was saved.",
                UnknownPaths = unknown.Select(path => path.ToString()).ToList()
            });
        }

        var stored = await _store
            .ReplaceAsync(paths, request.CoverNewMedia, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "DialogueBoost: selection saved — {Count} scope(s), new media {Coverage:l}.",
            stored.Scopes.Count,
            stored.CoverNewMedia ? "covered" : "not covered");

        return Ok(Describe(stored));
    }

    /// <summary>
    /// Gets what the selection covers in the library as it is now: per scope, and in total.
    /// </summary>
    /// <remarks>
    /// Separate from <c>GET /Selection</c> because it costs a query per scope, and reading the
    /// stored selection should not.
    /// </remarks>
    [HttpGet("/Plugins/DialogueBoost/Selection/Coverage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SelectionCoverageDto>> GetCoverage(CancellationToken cancellationToken)
    {
        var selection = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
        var resolved = _resolver.ResolveScopes(selection);

        return Ok(new SelectionCoverageDto
        {
            Scopes = resolved.Select(Describe).ToList(),
            TotalItems = resolved.SelectMany(r => r.Items).DistinctBy(i => i.Id).Count(),
            CoverNewMedia = selection.CoverNewMedia
        });
    }

    private SelectionDto Describe(LibrarySelection selection) => new()
    {
        Scopes = selection.Scopes
            .Select(scope => new SelectionScopeDto
            {
                Path = scope.Path.ToString(),
                AddedAtUtc = scope.AddedAtUtc
            })
            .ToList(),
        CoverNewMedia = selection.CoverNewMedia,
        UpdatedAtUtc = selection.UpdatedAtUtc,
        NewLibraryPaths = _newLibraries.Since(selection)
            .Select(library => ScopePath.Root.Append(ScopeSegment.ForItem(library.Id)).ToString())
            .ToList()
    };

    /// <summary>
    /// Describes one scope against the entities it resolves to now.
    /// </summary>
    private static ScopeCoverageDto Describe(ResolvedScope resolved)
    {
        var first = resolved.Nodes.Count > 0 ? resolved.Nodes[0] : null;

        return new ScopeCoverageDto
        {
            Path = resolved.Scope.Path.ToString(),
            AddedAtUtc = resolved.Scope.AddedAtUtc,
            Exists = first is not null,
            Name = first?.Name,
            Type = first?.GetBaseItemKind().ToString(),
            MemberCount = resolved.Nodes.Count,
            ItemCount = resolved.Items.Count
        };
    }
}
