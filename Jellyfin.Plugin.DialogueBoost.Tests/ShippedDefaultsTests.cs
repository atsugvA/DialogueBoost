using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using Jellyfin.Plugin.DialogueBoost.Integration;
using Jellyfin.Plugin.DialogueBoost.Processing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// What a server that has never opened the configuration page is running.
/// </summary>
/// <remarks>
/// These are not restatements of the property initialisers — they are the shipped product, and the
/// two that matter cannot be read off a single default. A fresh install must do something useful
/// without being configured, and must not do anything irreversible without being asked; those pull
/// in opposite directions, and where the line sits is a decision rather than a value.
/// </remarks>
public class ShippedDefaultsTests
{
    /// <summary>An installed server with real accounts, so "nobody counts" is a policy and not an empty server.</summary>
    private sealed class TwoAccountServer : IWatchedState
    {
        public static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid LivingRoom = Guid.Parse("22222222-2222-2222-2222-222222222222");

        public IReadOnlyList<ServerAccount> Accounts() => new[]
        {
            new ServerAccount(Owner, "owner", false),
            new ServerAccount(LivingRoom, "living room", false)
        };

        // Both accounts have played everything: if the policy read them, every item would be watched.
        public bool HasPlayed(Guid accountId, BaseItem item) => true;

        public IReadOnlySet<Guid> PlayedBy(Guid accountId) => new HashSet<Guid>();
    }

    private static BaseItem AnItem() => new Movie { Name = "Anything", Id = Guid.NewGuid() };

    /// <summary>
    /// The half that cannot be undone by re-running. Nothing is chosen, so nothing decides, so
    /// nothing is skipped for being watched and the cleanup deletes nothing — on a server whose
    /// every account has in fact played the item. Skipping and deleting are opted into by naming
    /// accounts; until then the plugin only adds.
    /// </summary>
    [Fact]
    public void OutOfTheBox_NoAccountDecidesWatched_SoNothingIsSkippedOrDeleted()
    {
        var config = new PluginConfiguration();

        Assert.Equal(WatchedByPolicy.ChosenAccounts, config.WatchedBy);
        Assert.Empty(config.WatchedByUserIds);

        var watched = new WatchedItems(new TwoAccountServer());
        Assert.Empty(watched.DecidingAccounts(config));
        Assert.False(watched.IsWatched(AnItem(), config));
    }

    /// <summary>
    /// And the half that must work unasked: enabled, one profile on, claiming the default track so
    /// the boosted track is the one that plays, and covering arrivals on its own.
    /// </summary>
    [Fact]
    public void OutOfTheBox_OneProfileIsOnAndClaimsTheTrackThatPlays()
    {
        var config = new PluginConfiguration();

        Assert.True(config.Enabled);

        var on = config.GetAllProfiles().Where(p => p.Enabled).ToList();
        var boost = Assert.Single(on);
        Assert.Equal("dialogue-boost", boost.Id);
        Assert.True(config.ClaimsDefaultTrack(boost));
        Assert.Equal(new[] { "eng" }, boost.ProcessLanguages);

        Assert.Equal(NewMediaTrigger.WhenSettled, config.NewMediaTrigger);
        Assert.Equal(10, config.NewMediaSettleMinutes);
    }

    /// <summary>
    /// Nothing is in scope until somebody chooses it, and a library added later is flagged rather
    /// than swept in. This is the only reason a fresh install processes nothing.
    /// </summary>
    [Fact]
    public void OutOfTheBox_NothingIsInScopeAndANewLibraryIsOnlyFlagged()
    {
        var config = new PluginConfiguration();

        Assert.Empty(config.SelectedLibraries);
        Assert.Equal(NewLibraryPolicy.FlagOnly, config.NewLibraryPolicy);
    }

    /// <summary>
    /// Costs on somebody else's server: two jobs, and held back while anyone is streaming.
    /// </summary>
    [Fact]
    public void OutOfTheBox_TheRunIsGentleOnTheServerItLandsOn()
    {
        var config = new PluginConfiguration();

        Assert.Equal(2, config.MaxConcurrentJobs);
        Assert.True(config.PauseDuringActivePlayback);
        Assert.False(config.DryRun);
    }

    /// <summary>
    /// Tracks go to Jellyfin's own folder for each item, where nothing else on the machine looks: a
    /// file beside the media is adopted by library managers as one of the video's extras, and
    /// renamed, moved and deleted with it. A configuration saved before the setting existed has no
    /// element for it, so this default is also what an upgraded server runs.
    /// </summary>
    [Fact]
    public void OutOfTheBox_TracksGoToJellyfinsOwnFolderForEachItem()
    {
        Assert.Equal(SidecarLocation.MetadataFolder, new PluginConfiguration().SidecarLocation);
    }
}
