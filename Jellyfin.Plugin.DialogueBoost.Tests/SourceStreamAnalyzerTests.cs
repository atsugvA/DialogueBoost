using System.Collections.Generic;
using Jellyfin.Plugin.DialogueBoost.Analysis;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using Jellyfin.Plugin.DialogueBoost.State;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class SourceStreamAnalyzerTests
{
    /// <summary>
    /// The state Jellyfin reports once a sidecar exists: the external tracks take the low indices
    /// and the source's own audio is pushed up. What the plugin maps and hashes must not move with
    /// them.
    /// </summary>
    [Fact]
    public void ExtractFromMediaStreams_SidecarPresent_LeavesTheSourceTrackOrdinalsAlone()
    {
        var before = SourceStreamAnalyzer.ExtractFromMediaStreams(new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video, Index = 0 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "deu", Channels = 6 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 2, Language = "eng", Channels = 2 }
        });

        // The same file, after its sidecar became visible — measured on a live item, whose embedded
        // audio moved from 1,2 to 3,4 while the external tracks took 0,1.
        var after = SourceStreamAnalyzer.ExtractFromMediaStreams(new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Audio, Index = 0, Language = "deu", IsExternal = true, Title = "Original" },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "deu", IsExternal = true, Title = "Dialogue Boost" },
            new MediaStream { Type = MediaStreamType.Video, Index = 2 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 3, Language = "deu", Channels = 6 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 4, Language = "eng", Channels = 2 }
        });

        Assert.Equal(new[] { 0, 1 }, before.ConvertAll(stream => stream.AudioIndex));
        Assert.Equal(new[] { 0, 1 }, after.ConvertAll(stream => stream.AudioIndex));

        var profile = new DialogueBoostProfile { ProcessLanguages = new List<string> { "deu" } };
        Assert.Equal(
            ProcessingStateRepository.ComputeParamsHash(profile, before),
            ProcessingStateRepository.ComputeParamsHash(profile, after));
    }

    /// <summary>
    /// The ordinal is what <c>-map 0:a:N</c> means, so it follows the container's order and not the
    /// order the caller's list happened to be in.
    /// </summary>
    [Fact]
    public void ExtractFromMediaStreams_UnorderedInput_NumbersByContainerIndex()
    {
        var result = SourceStreamAnalyzer.ExtractFromMediaStreams(new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Audio, Index = 4, Language = "eng" },
            new MediaStream { Type = MediaStreamType.Audio, Index = 3, Language = "deu" },
            new MediaStream { Type = MediaStreamType.Video, Index = 2 }
        });

        Assert.Equal(new[] { "deu", "eng" }, result.ConvertAll(stream => stream.Language));
        Assert.Equal(new[] { 0, 1 }, result.ConvertAll(stream => stream.AudioIndex));
    }

    [Fact]
    public void ExtractFromMediaStreams_IgnoresExternalStreams_AndAssignsSequentialAudioIndexToInternalStreams()
    {
        // Arrange: A media item with video, 2 internal audio streams, and 1 external sidecar audio stream
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video, Index = 0 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "ger", IsExternal = false },
            new MediaStream { Type = MediaStreamType.Audio, Index = 2, Language = "ger", IsExternal = true, Title = "Sidecar DB" },
            new MediaStream { Type = MediaStreamType.Audio, Index = 3, Language = "fre", IsExternal = false }
        };

        // Act
        var result = SourceStreamAnalyzer.ExtractFromMediaStreams(streams);

        // Assert
        Assert.Equal(2, result.Count);
        
        Assert.Equal(1, result[0].Index);
        Assert.Equal(0, result[0].AudioIndex);
        // Normalised on the way in, so this path and the ffprobe one give one answer for one file.
        Assert.Equal("deu", result[0].Language);
        Assert.False(result[0].IsExternal);

        Assert.Equal(3, result[1].Index);
        Assert.Equal(1, result[1].AudioIndex);
        Assert.Equal("fra", result[1].Language);
        Assert.False(result[1].IsExternal);
    }
}
