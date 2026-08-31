using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Jellyfin.Plugin.DialogueBoost.State;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class SelectionStoreTests : IDisposable
{
    private static readonly ScopePath Movies = Row("f137a2dd-21bb-c1b9-9aa5-c0f6bf02a805");
    private static readonly ScopePath Shows = Row("a656b907-eb3a-7353-2e40-e44b968d0225");
    private static readonly ScopePath Series = Row("b36ff9cd-98c4-7155-4d0e-6b69487e6e0b");

    private readonly string _dbPath;
    private readonly SelectionStore _store;

    private static ScopePath Row(string id) => ScopePath.Root.Append(ScopeSegment.ForItem(Guid.Parse(id)));

    public SelectionStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_selection_{Guid.NewGuid():N}.db");
        _store = new SelectionStore(new PluginDatabase(_dbPath));
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAsync_WithNothingStored_ReportsAnUnwrittenDocument()
    {
        var selection = await _store.GetAsync();

        Assert.Empty(selection.Scopes);
        Assert.True(selection.CoverNewMedia);
        Assert.Null(selection.UpdatedAtUtc);
    }

    [Fact]
    public async Task ReplaceAsync_StoresExactlyWhatWasChosen()
    {
        var stored = await _store.ReplaceAsync(new[] { Movies, Shows });

        Assert.Equal(
            new[] { Movies, Shows }.Select(p => p.ToString()).OrderBy(p => p),
            stored.Scopes.Select(s => s.Path.ToString()).OrderBy(p => p));
        Assert.NotNull(stored.UpdatedAtUtc);
        Assert.Equal(stored.Scopes.Count, (await _store.GetAsync()).Scopes.Count);
    }

    [Fact]
    public async Task ReplaceAsync_CollapsesDuplicatesAndDropsTheRoot()
    {
        var stored = await _store.ReplaceAsync(new[] { Movies, Movies, ScopePath.Root });

        Assert.Single(stored.Scopes);
        Assert.Equal(Movies, stored.Scopes[0].Path);
    }

    [Fact]
    public async Task ReplaceAsync_KeepsTheWatermarkOfAScopeThatSurvives()
    {
        var first = await _store.ReplaceAsync(new[] { Movies });
        var movies = first.Scopes.Single();

        var second = await _store.ReplaceAsync(new[] { Movies, Shows });

        Assert.Equal(movies.AddedAtUtc, second.Scopes.Single(s => s.Path.Equals(Movies)).AddedAtUtc);
        Assert.True(second.Scopes.Single(s => s.Path.Equals(Shows)).AddedAtUtc >= movies.AddedAtUtc);
    }

    [Fact]
    public async Task ReplaceAsync_ForgetsAScopeThatWasDeselected()
    {
        await _store.ReplaceAsync(new[] { Movies, Shows, Series });

        var stored = await _store.ReplaceAsync(new[] { Movies });

        Assert.Equal(Movies, Assert.Single(stored.Scopes).Path);
    }

    [Fact]
    public async Task ReplaceAsync_WithNothingChosen_ClearsTheSelectionButKeepsTheDocument()
    {
        await _store.ReplaceAsync(new[] { Movies });

        var stored = await _store.ReplaceAsync(Array.Empty<ScopePath>());

        Assert.Empty(stored.Scopes);
        Assert.NotNull(stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task ReplaceAsync_RoundTripsCoverNewMediaAndLeavesItAloneWhenNotGiven()
    {
        await _store.ReplaceAsync(new[] { Movies }, coverNewMedia: false);
        Assert.False((await _store.GetAsync()).CoverNewMedia);

        await _store.ReplaceAsync(new[] { Movies, Shows });
        Assert.False((await _store.GetAsync()).CoverNewMedia);

        await _store.ReplaceAsync(new[] { Movies }, coverNewMedia: true);
        Assert.True((await _store.GetAsync()).CoverNewMedia);
    }

    [Fact]
    public async Task ASecondStoreOnTheSameFileReadsTheSameSelection()
    {
        await _store.ReplaceAsync(new[] { Movies, Shows }, coverNewMedia: false);

        var reopened = await new SelectionStore(new PluginDatabase(_dbPath)).GetAsync();

        Assert.Equal(2, reopened.Scopes.Count);
        Assert.False(reopened.CoverNewMedia);
    }

    [Fact]
    public async Task RewriteAsync_KeepsEachWatermarkAndDoesNotCountAsASave()
    {
        // What SelectionUpgrade needs: a scope changes how it is spelled without changing what it
        // means. A fresh watermark would un-freeze a frozen scope, and a fresh UpdatedAtUtc would
        // mark every library added since the last save as already seen.
        await _store.ReplaceAsync(new[] { Movies, Shows });
        var before = await _store.GetAsync();
        var chosen = before.Scopes.Single(s => s.Path.Equals(Movies)).AddedAtUtc;

        var renamed = Movies.Append("k:Series|sparrow");
        var after = await _store.RewriteAsync(new[]
        {
            new SelectionScope(renamed, chosen),
            before.Scopes.Single(s => s.Path.Equals(Shows))
        });

        Assert.Equal(chosen, after.Scopes.Single(s => s.Path.Equals(renamed)).AddedAtUtc);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.DoesNotContain(after.Scopes, s => s.Path.Equals(Movies));
    }

    [Fact]
    public async Task ASelectionStoredAsItemIdsIsReadBackUnchanged()
    {
        // The schema before rows had paths. The column is renamed rather than rebuilt, so a bare id
        // survives as a one-segment path — still naming exactly the entity the user clicked, which
        // is what it named before.
        string legacyDb = Path.Combine(Path.GetTempPath(), $"test_legacy_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={legacyDb}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE selection_scopes (
                        item_id       TEXT NOT NULL PRIMARY KEY,
                        added_at_utc  TEXT NOT NULL
                    );
                    INSERT INTO selection_scopes VALUES ('4ddbc9394cca984d74f3fbbf88f39022', '2026-08-27T17:47:11.5973111Z');
                ";
                command.ExecuteNonQuery();
            }

            var selection = await new SelectionStore(new PluginDatabase(legacyDb)).GetAsync();

            var scope = Assert.Single(selection.Scopes);
            Assert.Equal("4ddbc9394cca984d74f3fbbf88f39022", scope.Path.ToString());
            Assert.Equal(
                Guid.Parse("4ddbc939-4cca-984d-74f3-fbbf88f39022"),
                ScopeSegment.ItemId(scope.Path.Segments[0]));
            Assert.Equal(new DateTime(2026, 8, 27, 17, 47, 11, DateTimeKind.Utc), scope.AddedAtUtc, TimeSpan.FromSeconds(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { legacyDb, legacyDb + "-wal", legacyDb + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentSavesAndReadsNeverProduceAMixtureOfTwoSelections()
    {
        // The failure this guards against is the reported one: a save that lands while another
        // writer is mid-flight, leaving a selection that was never chosen by anyone.
        var one = new[] { Movies };
        var two = new[] { Movies, Shows, Series };
        var readBack = new List<int>();

        // Seeded first, so that "nothing stored yet" is not one of the answers a reader may
        // legitimately see — every read below has to land on one whole selection or the other.
        await _store.ReplaceAsync(one);

        var work = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            if (i % 2 == 0)
            {
                await _store.ReplaceAsync(i % 4 == 0 ? one : two);
            }
            else
            {
                var selection = await _store.GetAsync();
                lock (readBack)
                {
                    readBack.Add(selection.Scopes.Count);
                }
            }
        }));

        await Task.WhenAll(work);

        Assert.All(readBack, count => Assert.Contains(count, new[] { one.Length, two.Length }));
        Assert.Contains((await _store.GetAsync()).Scopes.Count, new[] { one.Length, two.Length });
    }
}
