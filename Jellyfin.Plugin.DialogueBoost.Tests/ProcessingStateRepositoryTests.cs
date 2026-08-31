using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Jellyfin.Plugin.DialogueBoost.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class ProcessingStateRepositoryTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly ProcessingStateRepository _repo;

    public ProcessingStateRepositoryTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"test_dialogue_boost_{Guid.NewGuid():N}.db");
        _repo = new ProcessingStateRepository(_tempDbPath, NullLogger<ProcessingStateRepository>.Instance);
    }

    public void Dispose()
    {
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
    }

    [Fact]
    public async Task SaveRecordAsync_And_GetRecordAsync_PersistsAndRetrievesCorrectly()
    {
        var record = new ProcessedItemRecord
        {
            ItemId = "item123",
            ProfileId = "dialogue-boost",
            SourcePath = "/media/test.mkv",
            SourceMTimeUtc = DateTime.UtcNow.Ticks,
            SourceSizeBytes = 1024567,
            ParamsHash = "hashabc123",
            SidecarPath = "/media/test.Dialogue Boost.mka",
            ProcessedLanguages = new List<string> { "rus" },
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };

        await _repo.SaveRecordAsync(record);

        var retrieved = await _repo.GetRecordAsync("item123", "dialogue-boost");
        Assert.NotNull(retrieved);
        Assert.Equal("item123", retrieved.ItemId);
        Assert.Equal("dialogue-boost", retrieved.ProfileId);
        Assert.Equal("hashabc123", retrieved.ParamsHash);
        Assert.Single(retrieved.ProcessedLanguages);
        Assert.Equal("rus", retrieved.ProcessedLanguages[0]);
    }

    [Fact]
    public void ComputeParamsHash_SameInputs_ReturnsIdenticalHash()
    {
        var profile = new DialogueBoostProfile { CenterChannelGainDb = 4.0 };
        var streams = new List<AudioStreamInfo>
        {
            new AudioStreamInfo { Index = 0, Language = "rus", Codec = "ac3", Channels = 6, ChannelLayout = "5.1", IsDefault = true }
        };

        string hash1 = ProcessingStateRepository.ComputeParamsHash(profile, streams);
        string hash2 = ProcessingStateRepository.ComputeParamsHash(profile, streams);

        Assert.Equal(hash1, hash2);

        // Mutate parameter
        profile.CenterChannelGainDb = 6.0;
        string hash3 = ProcessingStateRepository.ComputeParamsHash(profile, streams);

        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public async Task GetRecordsWithSidecarsAsync_ReturnsOnlySuccessfulRecordsWithSidecarPath()
    {
        var record1 = new ProcessedItemRecord
        {
            ItemId = "item1",
            ProfileId = "p1",
            SourcePath = "/media/test1.mkv",
            SourceMTimeUtc = DateTime.UtcNow.Ticks,
            SourceSizeBytes = 100,
            ParamsHash = "hash1",
            SidecarPath = "/media/test1.Dialogue Boost.mka",
            ProcessedLanguages = new List<string> { "eng" },
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };

        var record2 = new ProcessedItemRecord
        {
            ItemId = "item2",
            ProfileId = "p1",
            SourcePath = "/media/test2.mkv",
            SourceMTimeUtc = DateTime.UtcNow.Ticks,
            SourceSizeBytes = 100,
            ParamsHash = "hash2",
            SidecarPath = "",
            ProcessedLanguages = new List<string>(),
            Status = "Failed",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };

        await _repo.SaveRecordAsync(record1);
        await _repo.SaveRecordAsync(record2);

        var sidecars = await _repo.GetRecordsWithSidecarsAsync();
        Assert.Single(sidecars);
        Assert.Equal("item1", sidecars[0].ItemId);
    }

    [Fact]
    public async Task DeleteAllRecordsForItemAsync_RemovesAllRecordsForMatchingItem()
    {
        var record1 = new ProcessedItemRecord
        {
            ItemId = "itemX",
            ProfileId = "p1",
            SourcePath = "/media/test.mkv",
            SourceMTimeUtc = DateTime.UtcNow.Ticks,
            SourceSizeBytes = 100,
            ParamsHash = "h1",
            SidecarPath = "/media/test.p1.mka",
            ProcessedLanguages = new List<string> { "eng" },
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };

        var record2 = new ProcessedItemRecord
        {
            ItemId = "itemX",
            ProfileId = "p2",
            SourcePath = "/media/test.mkv",
            SourceMTimeUtc = DateTime.UtcNow.Ticks,
            SourceSizeBytes = 100,
            ParamsHash = "h2",
            SidecarPath = "/media/test.p2.mka",
            ProcessedLanguages = new List<string> { "eng" },
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };

        await _repo.SaveRecordAsync(record1);
        await _repo.SaveRecordAsync(record2);

        int deleted = await _repo.DeleteAllRecordsForItemAsync("itemX");
        Assert.Equal(2, deleted);

        var check1 = await _repo.GetRecordAsync("itemX", "p1");
        Assert.Null(check1);
    }

    [Fact]
    public async Task SaveRecordAsync_PersistsExemptFromCleanupCorrectly()
    {
        var record = new ProcessedItemRecord
        {
            ItemId = "itemExempt",
            ProfileId = "p1",
            SourcePath = "/media/test.mkv",
            SourceMTimeUtc = DateTime.UtcNow.Ticks,
            SourceSizeBytes = 100,
            ParamsHash = "h1",
            SidecarPath = "/media/test.p1.mka",
            ProcessedLanguages = new List<string> { "eng" },
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks,
            ExemptFromCleanup = true
        };

        await _repo.SaveRecordAsync(record);

        var fetched = await _repo.GetRecordAsync("itemExempt", "p1");
        Assert.NotNull(fetched);
        Assert.True(fetched.ExemptFromCleanup);
    }

    /// <summary>
    /// A moved track keeps everything the record vouches for — the settings, the source, the
    /// protection from cleanup — and only its path changes, and only for the one profile.
    /// </summary>
    [Fact]
    public async Task UpdateSidecarPathAsync_MovesOneRecordsPathAndNothingElse()
    {
        ProcessedItemRecord Written(string profileId) => new()
        {
            ItemId = "itemMoved",
            ProfileId = profileId,
            SourcePath = "/media/film.mkv",
            SourceMTimeUtc = 42,
            SourceSizeBytes = 300,
            ParamsHash = "h-" + profileId,
            ProfileParamsHash = "p-" + profileId,
            SidecarPath = $"/media/film.{profileId}.mka",
            ProcessedLanguages = new List<string> { "eng" },
            Status = "Success",
            ProcessedAtUtc = 7,
            ExemptFromCleanup = true
        };

        await _repo.SaveRecordAsync(Written("p1"));
        await _repo.SaveRecordAsync(Written("p2"));

        await _repo.UpdateSidecarPathAsync("itemMoved", "p1", "/metadata/ab/film.p1.mka");

        var moved = await _repo.GetRecordAsync("itemMoved", "p1");
        Assert.NotNull(moved);
        Assert.Equal("/metadata/ab/film.p1.mka", moved.SidecarPath);
        Assert.Equal("h-p1", moved.ParamsHash);
        Assert.Equal("p-p1", moved.ProfileParamsHash);
        Assert.Equal("/media/film.mkv", moved.SourcePath);
        Assert.Equal("Success", moved.Status);
        Assert.True(moved.ExemptFromCleanup);
        Assert.Equal(7, moved.ProcessedAtUtc);

        var untouched = await _repo.GetRecordAsync("itemMoved", "p2");
        Assert.Equal("/media/film.p2.mka", untouched!.SidecarPath);
    }

    [Fact]
    public async Task SetExemptAsync_UpdatesExemptFlagSuccessfully()
    {
        var record = new ProcessedItemRecord
        {
            ItemId = "itemToggle",
            ProfileId = "p1",
            SourcePath = "/media/test2.mkv",
            SourceMTimeUtc = DateTime.UtcNow.Ticks,
            SourceSizeBytes = 200,
            ParamsHash = "h2",
            SidecarPath = "/media/test2.p1.mka",
            ProcessedLanguages = new List<string> { "eng" },
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks,
            ExemptFromCleanup = false
        };

        await _repo.SaveRecordAsync(record);

        bool updated = await _repo.SetExemptAsync("itemToggle", "p1", true);
        Assert.True(updated);

        var fetched = await _repo.GetRecordAsync("itemToggle", "p1");
        Assert.NotNull(fetched);
        Assert.True(fetched.ExemptFromCleanup);

        bool updatedBack = await _repo.SetExemptAsync("itemToggle", "p1", false);
        Assert.True(updatedBack);

        var fetched2 = await _repo.GetRecordAsync("itemToggle", "p1");
        Assert.NotNull(fetched2);
        Assert.False(fetched2.ExemptFromCleanup);
    }

    /// <summary>
    /// A known profile and track set, hashed by hand outside this codebase.
    /// </summary>
    /// <remarks>
    /// Every existing <c>processed_items</c> row is keyed on this hash, so a change to the string
    /// format re-encodes the whole library once — which is a cost to pay deliberately, never to
    /// discover. The expected values below were computed independently from the documented format,
    /// not captured from a previous run of this code.
    /// </remarks>
    private static (DialogueBoostProfile Profile, List<AudioStreamInfo> Streams) GoldenInput()
    {
        var profile = new DialogueBoostProfile
        {
            SetAsDefaultTrack = true,
            SidecarNamingMarker = "Dialogue Boost",
            ProcessLanguages = new List<string> { "deu" },
            CenterChannelGainDb = 4.0
        };

        var streams = new List<AudioStreamInfo>
        {
            new()
            {
                Index = 0, Language = "deu", Codec = "ac3",
                Channels = 6, ChannelLayout = "5.1", IsDefault = true
            }
        };

        return (profile, streams);
    }

    [Fact]
    public void ComputeParamsHash_MatchesTheDocumentedStringFormat()
    {
        var (profile, streams) = GoldenInput();

        Assert.Equal(
            "73805433a98251be682b8909f0c20dfae3446a61c95517c6ce20815cd39735d7",
            ProcessingStateRepository.ComputeParamsHash(profile, streams));
    }

    [Fact]
    public void ComputeProfileParamsHash_CoversTheParametersAndNotTheStreams()
    {
        var (profile, _) = GoldenInput();

        Assert.Equal(
            "17c4afe06bbabb9fa19ea660d56a77dcb8d2be389a8e410374ab86d96871195e",
            ProcessingStateRepository.ComputeProfileParamsHash(profile));
    }

    /// <summary>
    /// The resolved claim is not the same fact as the switch: disabling the profile that held it
    /// moves this one's filename with its own settings untouched. Both hashes have to notice, or a
    /// run rewrites a sidecar the forecast has already called done.
    /// </summary>
    [Fact]
    public void BothHashes_MoveWhenTheProfileTakesTheDefaultTrackClaim()
    {
        var (profile, streams) = GoldenInput();

        Assert.NotEqual(
            ProcessingStateRepository.ComputeParamsHash(profile, streams, claimsDefaultTrack: false),
            ProcessingStateRepository.ComputeParamsHash(profile, streams, claimsDefaultTrack: true));

        Assert.NotEqual(
            ProcessingStateRepository.ComputeProfileParamsHash(profile, claimsDefaultTrack: false),
            ProcessingStateRepository.ComputeProfileParamsHash(profile, claimsDefaultTrack: true));
    }

    [Fact]
    public void ComputeProfileParamsHash_IgnoresTheSource_ButNoticesTheSettings()
    {
        var (profile, streams) = GoldenInput();
        string before = ProcessingStateRepository.ComputeProfileParamsHash(profile);

        // A different source: the profile half must not move, the full hash must.
        streams[0].Channels = 2;
        streams[0].ChannelLayout = "stereo";

        Assert.Equal(before, ProcessingStateRepository.ComputeProfileParamsHash(profile));

        // A different setting: the profile half must move.
        profile.CenterChannelGainDb = 6.0;

        Assert.NotEqual(before, ProcessingStateRepository.ComputeProfileParamsHash(profile));
    }

    [Fact]
    public async Task SaveRecordAsync_RoundTripsTheProfileParamsHash()
    {
        var record = new ProcessedItemRecord
        {
            ItemId = "hash-item",
            ProfileId = "dialogue-boost",
            SourcePath = "/media/x.mkv",
            SourceMTimeUtc = 1,
            SourceSizeBytes = 2,
            ParamsHash = "full",
            ProfileParamsHash = "profile-half",
            SidecarPath = "/media/x.Dialogue Boost.mka",
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };

        await _repo.SaveRecordAsync(record);
        var read = await _repo.GetRecordAsync("hash-item", "dialogue-boost");

        Assert.NotNull(read);
        Assert.Equal("profile-half", read!.ProfileParamsHash);
    }

    [Fact]
    public async Task GetRecordAsync_ReturnsNullProfileParamsHash_ForARecordWrittenWithoutOne()
    {
        var record = new ProcessedItemRecord
        {
            ItemId = "old-item",
            ProfileId = "dialogue-boost",
            SourcePath = "/media/old.mkv",
            SourceMTimeUtc = 1,
            SourceSizeBytes = 2,
            ParamsHash = "full",
            SidecarPath = "/media/old.Dialogue Boost.mka",
            Status = "Success",
            ProcessedAtUtc = DateTime.UtcNow.Ticks
        };

        await _repo.SaveRecordAsync(record);
        var read = await _repo.GetRecordAsync("old-item", "dialogue-boost");

        Assert.NotNull(read);
        Assert.Null(read!.ProfileParamsHash);
    }
}
