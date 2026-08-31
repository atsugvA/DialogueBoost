using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DialogueBoost.Api;

/// <summary>
/// The server's accounts, so the page can ask which of them decide that an item has been watched.
/// </summary>
/// <remarks>
/// Jellyfin's own <c>/Users</c> would answer this, but it answers with far more than a name and
/// needs its own permissions; the page needs three fields and the plugin already reads them through
/// its seam.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class AccountsController : ControllerBase
{
    private readonly IWatchedState _state;
    private readonly WatchedItems _watched;

    public AccountsController(IWatchedState state, WatchedItems watched)
    {
        _state = state;
        _watched = watched;
    }

    /// <summary>
    /// Every account, and which of them the current configuration actually reads.
    /// </summary>
    [HttpGet("/Plugins/DialogueBoost/Accounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetAccounts()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var deciding = _watched.DecidingAccounts(config).ToHashSet();

        return Ok(new
        {
            Accounts = _state.Accounts()
                .OrderBy(account => account.Name, System.StringComparer.OrdinalIgnoreCase)
                .Select(account => new
                {
                    account.Id,
                    account.Name,
                    account.IsDisabled,
                    Deciding = deciding.Contains(account.Id)
                })
                .ToList()
        });
    }
}
