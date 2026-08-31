using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// Whether anybody still needs a boosted track for an item.
/// </summary>
/// <remarks>
/// One question asked in two places — before making a sidecar, and before deleting one — which
/// until now was two copies of the same loop, both reading "watched" as *any* account having played
/// it. On a server with more than one account that answer deletes a track other people are
/// using. Here it is one policy, configured once: an item is watched when every account that counts
/// has played it, and which accounts count is <see cref="WatchedByPolicy"/>.
/// </remarks>
public sealed class WatchedItems
{
    private readonly IWatchedState _state;

    public WatchedItems(IWatchedState state)
    {
        _state = state;
    }

    /// <summary>
    /// Whether every account that counts has played this item. False when no account counts, which
    /// is what makes an unconfigured server never delete anything.
    /// </summary>
    public bool IsWatched(BaseItem item, PluginConfiguration config)
    {
        var deciding = DecidingAccounts(config);
        return deciding.Count > 0 && deciding.All(accountId => _state.HasPlayed(accountId, item));
    }

    /// <summary>
    /// Which of these items every deciding account has played.
    /// </summary>
    /// <remarks>
    /// The same answer as <see cref="IsWatched"/> for each item, reached in one question per
    /// account rather than one per item. The page's headline number asks this about everything the
    /// selection covers, and asking per item cost 3.2 seconds over 284 items — enough that the band
    /// could not re-read its own numbers while a run was moving them.
    /// </remarks>
    public IReadOnlySet<Guid> WatchedAmong(IReadOnlyCollection<BaseItem> items, PluginConfiguration config)
    {
        var deciding = DecidingAccounts(config);
        if (deciding.Count == 0 || items.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var played = deciding.Select(accountId => _state.PlayedBy(accountId)).ToList();
        return items
            .Where(item => played.All(seen => seen.Contains(item.Id)))
            .Select(item => item.Id)
            .ToHashSet();
    }

    /// <summary>
    /// The accounts whose viewing history the current configuration says to read.
    /// </summary>
    public IReadOnlyList<Guid> DecidingAccounts(PluginConfiguration config)
    {
        if (config.WatchedBy == WatchedByPolicy.ChosenAccounts)
        {
            // Only accounts the server still has: one deleted since it was chosen must not hold
            // every item unwatched forever.
            var existing = _state.Accounts().Select(account => account.Id).ToHashSet();
            return config.WatchedByUserIds.Where(existing.Contains).ToList();
        }

        return _state.Accounts()
            .Where(account => !account.IsDisabled)
            .Select(account => account.Id)
            .ToList();
    }
}
