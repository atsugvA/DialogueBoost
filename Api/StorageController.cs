using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Api.Models;
using Jellyfin.Plugin.DialogueBoost.Configuration;
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
    private readonly SidecarPlacement _placement;

    public StorageController(SelectionStore store, ScopeResolver resolver, StorageProbe probe, SidecarPlacement placement)
    {
        _store = store;
        _resolver = resolver;
        _probe = probe;
        _placement = placement;
    }

    /// <summary>
    /// Probes the folders the current selection's tracks would be written into.
    /// </summary>
    [HttpGet("/Plugins/DialogueBoost/Storage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<StorageReportDto>> GetStorage(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var location = config.SidecarLocation;
        var selection = await _store.GetAsync(cancellationToken).ConfigureAwait(false);

        // The folders a run writes into. Beside the media that is the source's own folder, and one
        // that is missing means a volume that is not mounted. An item's metadata folder is often
        // missing for a better reason — Jellyfin makes it the first time it stores something, and a
        // run makes it for a track — so what decides there is the nearest folder above it that is.
        var folders = _resolver.ResolveItems(selection)
            .Select(item => _placement.FolderFor(item, location))
            .Select(folder => location == SidecarLocation.MetadataFolder ? StorageProbe.NearestExisting(folder) : folder)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(System.StringComparer.Ordinal)
            .ToList();

        // Asked for by a person, who has quite possibly just fixed the permissions and wants to
        // know — so the answer is probed now rather than remembered from a minute ago.
        _probe.Forget();

        var results = folders.Take(MaxFolders).Select(directory => _probe.Check(directory)).ToList();

        return Ok(new StorageReportDto
        {
            Location = location.ToString(),
            FreeBytes = results.Select(r => r.FreeBytes).Where(free => free is not null).Min(),
            ReserveBytes = location == SidecarLocation.MetadataFolder ? Processing.ItemGate.JellyfinDiskReserveBytes : 0,
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
