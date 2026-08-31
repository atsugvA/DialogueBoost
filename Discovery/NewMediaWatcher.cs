using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using Jellyfin.Plugin.DialogueBoost.Selection;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Jellyfin.Plugin.DialogueBoost.Discovery;

/// <summary>
/// Notices media added to the library while the server is running, so that files downloaded after
/// the last run can be covered without anyone opening the config page.
/// </summary>
/// <remarks>
/// Deliberately an accelerator, not a source of truth. What a run processes is resolved from the
/// selected scopes at run time, so an event this never sees — server down, real-time
/// monitoring off for a library, files written from the Windows side of the disk — costs a delay
/// and nothing else.
/// </remarks>
public sealed class NewMediaWatcher : IHostedService, IDisposable
{
    /// <summary>How many item names to name in the log line before summarising the rest.</summary>
    private const int NamesLogged = 5;

    private readonly IMediaLibrary _library;
    private readonly SelectionStore _selectionStore;
    private readonly ScopeResolver _resolver;
    private readonly NewLibraries _newLibraries;
    private readonly ITaskManager _taskManager;
    private readonly DailySchedule _schedule;
    private readonly ILogger<NewMediaWatcher> _logger;
    private readonly ArrivalBuffer _arrivals;

    public NewMediaWatcher(
        IMediaLibrary library,
        SelectionStore selectionStore,
        ScopeResolver resolver,
        NewLibraries newLibraries,
        ITaskManager taskManager,
        DailySchedule schedule,
        ILogger<NewMediaWatcher> logger)
    {
        _library = library;
        _selectionStore = selectionStore;
        _resolver = resolver;
        _newLibraries = newLibraries;
        _taskManager = taskManager;
        _schedule = schedule;
        _logger = logger;
        _arrivals = new ArrivalBuffer(SettleWindow, OnArrivalsSettledAsync);
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Read per arrival, so changing it on the config page takes effect at once.
    /// </summary>
    private static TimeSpan SettleWindow() => TimeSpan.FromMinutes(Config.NewMediaSettleMinutes);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _logger.LogInformation(
            "DialogueBoost: watching for new media — {Trigger:l}, settle window {Minutes} minute(s).",
            Config.NewMediaTrigger == NewMediaTrigger.WhenSettled
                ? "a run starts once arrivals go quiet"
                : "arrivals wait for the next scheduled run",
            Config.NewMediaSettleMinutes);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // Fires for every entity type — Series, Season, Person, Studio — and from several threads at
        // once during a scan. Filter first, then let the buffer absorb the burst.
        if (!ProcessableItem.Is(e?.Item))
        {
            return;
        }

        _arrivals.Add(e!.Item.Id);
    }

    private async Task OnArrivalsSettledAsync(IReadOnlyCollection<Guid> itemIds)
    {
        try
        {
            var selection = await _selectionStore.GetAsync(CancellationToken.None).ConfigureAwait(false);

            // Media in a library added since the selection was saved is covered by nothing until
            // the policy says otherwise, so ask it here too rather than only at the next run.
            selection = await _newLibraries
                .ApplyPolicyAsync(selection, Config.NewLibraryPolicy, CancellationToken.None)
                .ConfigureAwait(false);

            var covered = Covered(selection, itemIds);

            _logger.LogInformation(
                "DialogueBoost: {Count} new item(s) in the library, {Covered} inside the selection: {Items}",
                itemIds.Count,
                covered.Count,
                Describe(itemIds));

            if (covered.Count == 0)
            {
                return;
            }

            if (Config.NewMediaTrigger != NewMediaTrigger.WhenSettled)
            {
                _logger.LogInformation("DialogueBoost: {Next:l}", NextRun());
                return;
            }

            if (!Config.Enabled)
            {
                _logger.LogInformation("DialogueBoost is disabled, so the new media is left where it is.");
                return;
            }

            // Queued rather than executed: a run already under way resolved its scopes before these
            // items existed, so what is wanted is another run after it, not an exception.
            _logger.LogInformation(
                "DialogueBoost: arrivals have settled — starting the normalization task for {Count} new item(s).",
                covered.Count);

            _taskManager.QueueScheduledTask<NormalizeLibraryTask>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DialogueBoost: failed to act on newly added items.");
        }
    }

    /// <summary>
    /// Which of the arrivals a run would actually pick up: inside a chosen scope, and not excluded
    /// by scopes that were frozen when they were chosen.
    /// </summary>
    private IReadOnlyCollection<Guid> Covered(LibrarySelection selection, IReadOnlyCollection<Guid> itemIds)
    {
        if (selection.IsEmpty || !selection.CoverNewMedia)
        {
            // Frozen scopes cover what they held when they were chosen, so nothing that arrives now
            // belongs to one — starting a run for it would find nothing to do.
            return Array.Empty<Guid>();
        }

        // The run's own answer rather than a second one: what the scopes resolve to right now is
        // exactly what the next run will look at. Asking instead where each arrival sits in the tree
        // would be a reimplementation that can drift — and a wrong one, since an item can sit under
        // two rows at once, a season and the release folder it arrived in.
        var covered = _resolver.ResolveScopes(selection)
            .SelectMany(resolved => resolved.Items)
            .Select(item => item.Id)
            .ToHashSet();

        return itemIds.Where(covered.Contains).ToList();
    }

    /// <summary>
    /// What will pick the arrivals up, in the words of Jellyfin's own schedule — including the case
    /// where nothing will.
    /// </summary>
    private string NextRun()
    {
        var at = _schedule.Read(PluginTasks.NormalizeKey);

        return at is TimeSpan time
            ? $"they will be covered by the daily run at {DailySchedule.Format(time)}."
            : "no run is scheduled, so nothing will cover them until the normalization task is started.";
    }

    /// <summary>
    /// Names the first few arrivals. Resolving every id would put a batch-sized burst of lookups on
    /// the library for a log line.
    /// </summary>
    private string Describe(IReadOnlyCollection<Guid> itemIds)
    {
        var names = itemIds
            .Take(NamesLogged)
            .Select(id => _library.ById(id)?.Name ?? id.ToString("N"))
            .ToList();

        int remaining = itemIds.Count - names.Count;
        return remaining > 0
            ? string.Join(", ", names) + $" (+{remaining} more)"
            : string.Join(", ", names);
    }

    public void Dispose() => _arrivals.Dispose();
}
