using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DialogueBoost.EntryPoints;

/// <summary>
/// Brings a selection stored by an earlier version forward, on the first start after the upgrade.
/// </summary>
/// <remarks>
/// Two shapes have to be caught up with, in this order because the second consumes the first:
///
/// 1. A selection in the plugin's XML configuration, from before it had a store of its own. The
///    marker is the stored document itself — while the selection has never been written, the
///    configuration is the only place one could have come from. Once it has been written, including
///    a deliberate save of nothing at all, the import never runs again, so a cleared selection stays
///    cleared across restarts.
/// 2. A selection of bare item ids, from before the plugin served its own tree. Those are placed in
///    the tree by <see cref="SelectionUpgrade"/>, which does nothing if the library is not readable
///    yet — in which case the first config-page visit does it instead, and until then the ids go on
///    covering exactly what they covered.
/// </remarks>
public sealed class LegacyUpgradeEntryPoint : IHostedService
{
    private readonly SelectionStore _selectionStore;
    private readonly SelectionUpgrade _upgrade;
    private readonly ILogger<LegacyUpgradeEntryPoint> _logger;

    public LegacyUpgradeEntryPoint(
        SelectionStore selectionStore,
        SelectionUpgrade upgrade,
        ILogger<LegacyUpgradeEntryPoint> logger)
    {
        _selectionStore = selectionStore;
        _upgrade = upgrade;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ImportFromConfigurationAsync(cancellationToken).ConfigureAwait(false);
            await _upgrade.ApplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed upgrade must not take the server's startup with it: the plugin still runs,
            // and the selection is one page visit away from being read and placed again.
            _logger.LogError(ex, "DialogueBoost: failed to bring a selection stored by an earlier version forward.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ImportFromConfigurationAsync(CancellationToken cancellationToken)
    {
        var selection = await _selectionStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (selection.UpdatedAtUtc is not null)
        {
            return;
        }

        var legacy = Plugin.Instance?.Configuration?.SelectedLibraries ?? new List<Guid>();

        // Stored as bare ids: each names exactly the entity the user clicked, which is what it named
        // before.
        await _selectionStore
            .ReplaceAsync(
                legacy.Select(id => ScopePath.Root.Append(ScopeSegment.ForItem(id))).ToList(),
                coverNewMedia: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (legacy.Count > 0)
        {
            _logger.LogInformation(
                "DialogueBoost: imported {Count} selected item(s) from the plugin configuration into the selection store.",
                legacy.Count);
        }
    }
}
