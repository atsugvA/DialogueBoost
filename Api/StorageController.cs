using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Api.Models;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DialogueBoost.Api;

/// <summary>
/// Reports whether the plugin can write where it is being asked to write.
/// </summary>
/// <remarks>
/// Exists because the failure it reports used to arrive as a generic ffmpeg error, hours into a run
/// and with nothing in it about permissions.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class StorageController : ControllerBase
{
    /// <summary>
    /// How many folders one check looks at. A selection can cover thousands, and the point here is
    /// an answer while the page is loading, not an exhaustive audit — the run itself probes every
    /// folder it writes into.
    /// </summary>
    private const int MaxFolders = 200;

    private readonly SelectionStore _store;
    private readonly ScopeResolver _resolver;
    private readonly StorageProbe _probe;

    public StorageController(SelectionStore store, ScopeResolver resolver, StorageProbe probe)
    {
        _store = store;
        _resolver = resolver;
        _probe = probe;
    }

    /// <summary>
    /// Probes the folders behind the current selection.
    /// </summary>
    [HttpGet("/Plugins/DialogueBoost/Storage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<StorageReportDto>> GetStorage(CancellationToken cancellationToken)
    {
        var selection = await _store.GetAsync(cancellationToken).ConfigureAwait(false);

        var folders = _resolver.ResolveItems(selection)
            .Select(item => Path.GetDirectoryName(item.Path))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(System.StringComparer.Ordinal)
            .ToList();

        // Asked for by a person, who has quite possibly just fixed the permissions and wants to
        // know — so the answer is probed now rather than remembered from a minute ago.
        _probe.Forget();

        var results = folders.Take(MaxFolders).Select(directory => _probe.Check(directory)).ToList();

        return Ok(new StorageReportDto
        {
            ServiceUser = StorageProbe.ServiceUser,
            FoldersChecked = results.Count,
            FoldersWritable = results.Count(r => r.IsWritable),
            Truncated = folders.Count > MaxFolders,
            Problems = results
                .Where(r => !r.IsWritable)
                .Select(r => new StorageProblemDto
                {
                    Directory = r.Directory,
                    State = r.State.ToString(),
                    Explanation = r.Explain()
                })
                .ToList()
        });
    }
}
