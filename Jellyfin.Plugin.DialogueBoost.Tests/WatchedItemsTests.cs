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

public class WatchedItemsTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LivingRoom = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Guest = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Retired = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>A server with three live accounts, and whoever has played the one item.</summary>
    private sealed class FakeWatchedState : IWatchedState
    {
        private readonly HashSet<Guid> _played;
        private readonly IReadOnlyList<ServerAccount> _accounts;

        public FakeWatchedState(IEnumerable<Guid> played, params ServerAccount[] accounts)
        {
            _played = played.ToHashSet();
            _accounts = accounts.Length > 0
                ? accounts
                : new[]
                {
                    new ServerAccount(Owner, "owner", false),
                    new ServerAccount(LivingRoom, "living room", false),
                    new ServerAccount(Guest, "guest", false)
                };
        }

        /// <summary>Which items each account has played, where a test cares which.</summary>
        public Dictionary<Guid, HashSet<Guid>> PlayedItems { get; } = new();

        public IReadOnlyList<ServerAccount> Accounts() => _accounts;

        public bool HasPlayed(Guid accountId, BaseItem item) => _played.Contains(accountId);

        public IReadOnlySet<Guid> PlayedBy(Guid accountId) =>
            PlayedItems.TryGetValue(accountId, out var ids) ? ids : new HashSet<Guid>();
    }

    private static BaseItem AnItem() => new Movie { Name = "DB M5 Control", Id = Guid.NewGuid() };

    private static PluginConfiguration Chosen(params Guid[] ids) => new()
    {
        WatchedBy = WatchedByPolicy.ChosenAccounts,
        WatchedByUserIds = ids.ToList()
    };

    [Fact]
    public void OneAccountFinishingIt_DoesNotMakeItWatchedForTheOthers()
    {
        var state = new FakeWatchedState(new[] { Owner });

        Assert.False(new WatchedItems(state).IsWatched(AnItem(), Chosen(Owner, LivingRoom)));
    }

    [Fact]
    public void EveryChosenAccountFinishingIt_MakesItWatched()
    {
        var state = new FakeWatchedState(new[] { Owner, LivingRoom });

        Assert.True(new WatchedItems(state).IsWatched(AnItem(), Chosen(Owner, LivingRoom)));
    }

    /// <summary>
    /// The reason the setting exists: an automation login that never watches anything must not hold
    /// every item unwatched forever.
    /// </summary>
    [Fact]
    public void AnAccountThatWasNotChosen_DoesNotHoldItUnwatched()
    {
        var state = new FakeWatchedState(new[] { Owner, LivingRoom });

        Assert.True(new WatchedItems(state).IsWatched(AnItem(), Chosen(Owner, LivingRoom)));
        Assert.False(new WatchedItems(state).IsWatched(AnItem(), Chosen(Owner, LivingRoom, Guest)));
    }

    [Fact]
    public void ChoosingNoAccount_MeansNothingIsEverWatched()
    {
        var state = new FakeWatchedState(new[] { Owner, LivingRoom, Guest });

        Assert.False(new WatchedItems(state).IsWatched(AnItem(), Chosen()));
    }

    /// <summary>An account deleted since it was chosen must not freeze the answer at "not watched".</summary>
    [Fact]
    public void AChosenAccountTheServerNoLongerHas_IsIgnored()
    {
        var state = new FakeWatchedState(new[] { Owner });
        var config = Chosen(Owner, Retired);

        Assert.Equal(new[] { Owner }, new WatchedItems(state).DecidingAccounts(config));
        Assert.True(new WatchedItems(state).IsWatched(AnItem(), config));
    }

    [Fact]
    public void EveryActiveAccount_SkipsDisabledOnesAndNeedsAllTheRest()
    {
        var accounts = new[]
        {
            new ServerAccount(Owner, "owner", false),
            new ServerAccount(LivingRoom, "living room", false),
            new ServerAccount(Retired, "old", true)
        };
        var config = new PluginConfiguration { WatchedBy = WatchedByPolicy.EveryActiveAccount };

        Assert.False(new WatchedItems(new FakeWatchedState(new[] { Owner }, accounts)).IsWatched(AnItem(), config));
        Assert.True(new WatchedItems(new FakeWatchedState(new[] { Owner, LivingRoom }, accounts)).IsWatched(AnItem(), config));
    }

    /// <summary>
    /// The default is deliberately the safe one: with no account enabled there is nobody to decide,
    /// and an undecided item is never skipped and never cleaned up.
    /// </summary>
    [Fact]
    public void NoAccountAtAll_IsNotWatched()
    {
        var state = new FakeWatchedState(Array.Empty<Guid>(), new ServerAccount(Retired, "old", true));

        Assert.False(new WatchedItems(state).IsWatched(AnItem(), new PluginConfiguration()));
    }

    /// <summary>
    /// The batch form is the same rule as <c>IsWatched</c>, asked once per account: an item counts
    /// only where every deciding account has played it.
    /// </summary>
    [Fact]
    public void WatchedAmong_TakesOnlyWhatEveryDecidingAccountHasPlayed()
    {
        var both = AnItem();
        var onlyOwner = AnItem();
        var neither = AnItem();

        var state = new FakeWatchedState(Array.Empty<Guid>());
        state.PlayedItems[Owner] = new HashSet<Guid> { both.Id, onlyOwner.Id };
        state.PlayedItems[LivingRoom] = new HashSet<Guid> { both.Id };

        var watched = new WatchedItems(state)
            .WatchedAmong(new[] { both, onlyOwner, neither }, Chosen(Owner, LivingRoom));

        Assert.Equal(new[] { both.Id }, watched);
    }

    /// <summary>
    /// An account that has played something outside the question is not a reason to widen the
    /// answer: only the items asked about come back.
    /// </summary>
    [Fact]
    public void WatchedAmong_AnswersAboutTheItemsItWasGiven()
    {
        var asked = AnItem();
        var elsewhere = AnItem();

        var state = new FakeWatchedState(Array.Empty<Guid>());
        state.PlayedItems[Owner] = new HashSet<Guid> { asked.Id, elsewhere.Id };

        var watched = new WatchedItems(state).WatchedAmong(new[] { asked }, Chosen(Owner));

        Assert.Equal(new[] { asked.Id }, watched);
    }

    [Fact]
    public void WatchedAmong_IsEmptyWhenNoAccountDecides()
    {
        var item = AnItem();
        var state = new FakeWatchedState(Array.Empty<Guid>());
        state.PlayedItems[Owner] = new HashSet<Guid> { item.Id };

        Assert.Empty(new WatchedItems(state).WatchedAmong(new[] { item }, Chosen()));
    }
}
