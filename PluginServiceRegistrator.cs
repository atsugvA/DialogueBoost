using System;
using System.Threading;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Discovery;
using Jellyfin.Plugin.DialogueBoost.EntryPoints;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Output;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DialogueBoost;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<IEncoderTools, JellyfinEncoderTools>();
        services.AddSingleton<IMediaLibrary, JellyfinMediaLibrary>();
        services.AddSingleton<IWatchedState, JellyfinWatchedState>();
        services.AddSingleton<IMetadataRefresher, JellyfinMetadataRefresher>();
        services.AddSingleton<IPlaybackSessions, JellyfinPlaybackSessions>();
        services.AddSingleton<WatchedItems>();
        services.AddSingleton<PluginDatabase>();
        services.AddSingleton<SelectionStore>();
        services.AddSingleton<ScopeResolver>();
        services.AddSingleton<ItemContainers>();
        services.AddSingleton<LibraryTree>();
        services.AddSingleton<NewLibraries>();
        services.AddSingleton<SelectionUpgrade>();
        services.AddSingleton<SourceStreamAnalyzer>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<StorageProbe>();
        services.AddSingleton<SidecarPlacement>();
        services.AddSingleton<SidecarWriter>();
        services.AddSingleton<SidecarSweep>();
        services.AddSingleton<ProcessingStateRepository>();
        services.AddSingleton<WorkForecaster>();
        services.AddSingleton<ItemGate>();
        services.AddSingleton<CandidateStreams>();
        services.AddSingleton<SidecarBookkeeping>();
        services.AddSingleton<ItemProcessor>();
        services.AddHostedService<LegacyUpgradeEntryPoint>();
        services.AddHostedService<StartupEntryPoint>();
        services.AddHostedService<NewMediaWatcher>();

        services.AddSingleton<JobConcurrencyManager>();
        services.AddSingleton<CleanupRunOverride>();
        services.AddSingleton<DailySchedule>();
    }
}
