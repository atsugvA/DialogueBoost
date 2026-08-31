using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.Integration;

/// <summary>One of the server's accounts, as much of it as this plugin needs.</summary>
public sealed record ServerAccount(Guid Id, string Name, bool IsDisabled);

/// <summary>
/// Who the server's accounts are, and what they have played. The plugin's only contact with
/// <see cref="IUserManager"/> and <see cref="IUserDataManager"/>.
/// </summary>
public interface IWatchedState
{
    /// <summary>Every account on the server, disabled ones included and marked as such.</summary>
    IReadOnlyList<ServerAccount> Accounts();

    /// <summary>
    /// Whether one account has played an item. False for an account that no longer exists, and
    /// false — not an exception — if Jellyfin cannot answer.
    /// </summary>
    bool HasPlayed(Guid accountId, BaseItem item);

    /// <summary>
    /// Everything one account has played, of any kind, as ids.
    /// </summary>
    /// <remarks>
    /// The same question as <see cref="HasPlayed"/>, asked once instead of once per item. Jellyfin
    /// answers the per-item form out of the user-data store, one round trip each: over the 284
    /// items of this library that is 3.2 seconds, and it is the whole cost of the page's headline
    /// number. Ids of kinds this plugin never processes come back too — a series somebody finished
    /// is one row — and cost nothing, because the caller only ever looks up items it holds.
    /// </remarks>
    IReadOnlySet<Guid> PlayedBy(Guid accountId);
}

/// <inheritdoc />
public sealed class JellyfinWatchedState : IWatchedState
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<JellyfinWatchedState> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinWatchedState"/> class.
    /// </summary>
    /// <remarks>
    /// Three services rather than two, because Jellyfin answers "has this account played it" and
    /// "what has this account played" through different doors: the first is the user-data store,
    /// the second is the library's own query engine. Both are the same fact, and both belong to
    /// this adapter — see <see cref="PlayedBy"/>.
    /// </remarks>
    public JellyfinWatchedState(
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        ILogger<JellyfinWatchedState> logger)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public IReadOnlyList<ServerAccount> Accounts() =>
        _userManager.GetUsers()
            .Select(user => new ServerAccount(user.Id, user.Username, IsDisabled(user)))
            .ToList();

    /// <summary>
    /// Read off the permission rows rather than through a DTO: nothing else about the account is
    /// wanted, and an account whose permissions did not load is treated as enabled — which errs
    /// toward counting it, and so toward keeping a sidecar rather than deleting one.
    /// </summary>
    private static bool IsDisabled(User user) =>
        user.Permissions?.Any(permission => permission.Kind == PermissionKind.IsDisabled && permission.Value) == true;

    public bool HasPlayed(Guid accountId, BaseItem item)
    {
        try
        {
            var user = _userManager.GetUserById(accountId);
            return user is not null && _userDataManager.GetUserData(user, item)?.Played == true;
        }
        catch (Exception ex)
        {
            // Unreadable is not watched: the caller either skips work it could have done, or keeps
            // a sidecar it could have deleted. Both are recoverable; deleting on a bad read is not.
            _logger.LogWarning(ex, "DialogueBoost: could not read the watched state of '{Item:l}'.", item.Name);
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<Guid> PlayedBy(Guid accountId)
    {
        var user = _userManager.GetUserById(accountId);
        if (user is null)
        {
            return new HashSet<Guid>();
        }

        var query = new InternalItemsQuery(user)
        {
            IsPlayed = true,
            Recursive = true
        };

        return _libraryManager.GetItemIds(query).ToHashSet();
    }
}
