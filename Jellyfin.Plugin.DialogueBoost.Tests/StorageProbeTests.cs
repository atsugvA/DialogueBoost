using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.DialogueBoost.Output;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class StorageProbeTests
{
    private static readonly string[] MountInfo =
    {
        "25 30 0:23 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw",
        "36 25 8:1 / /mnt/media rw,relatime shared:33 - ntfs3 /dev/sdb1 rw,uid=1000,gid=1000",
        "40 25 8:19 / /mnt/archive ro,relatime shared:41 - fuseblk /dev/sdc2 ro,user_id=0",
        "44 25 8:33 / /mnt/mediavault rw,relatime shared:45 - ext4 /dev/sdd2 rw"
    };

    private static bool? ReadOnly(string path, out string? mountPoint) =>
        StorageProbe.IsReadOnlyMount(MountInfo, path, out mountPoint);

    [Fact]
    public void APathIsMatchedToItsClosestMountPoint()
    {
        Assert.False(ReadOnly("/mnt/media/Shows/Show/Episode", out var mount));
        Assert.Equal("/mnt/media", mount);
    }

    [Fact]
    public void AReadOnlyMountIsSeenAsReadOnly()
    {
        Assert.True(ReadOnly("/mnt/archive/Media", out var mount));
        Assert.Equal("/mnt/archive", mount);
    }

    [Fact]
    public void ALongerMountPointWinsOverAShorterOne()
    {
        // /mnt/media and / both contain the path; only the nearest describes it.
        Assert.False(ReadOnly("/mnt/media", out var mount));
        Assert.Equal("/mnt/media", mount);
    }

    [Fact]
    public void AMountPointIsMatchedBySegment()
    {
        // /mnt/media must not claim /mnt/mediavault — a prefix match on the raw string would.
        ReadOnly("/mnt/mediavault/thing", out var mount);
        Assert.Equal("/mnt/mediavault", mount);
    }

    [Fact]
    public void APathOnNoKnownMountFallsBackToTheRoot()
    {
        Assert.False(ReadOnly("/var/lib/jellyfin", out var mount));
        Assert.Equal("/", mount);
    }

    [Fact]
    public void WithNoMountInformationNothingIsClaimed()
    {
        // Every platform without /proc/self/mountinfo lands here: writable is still told from not,
        // read-only just cannot be named as its own case.
        Assert.Null(StorageProbe.IsReadOnlyMount(Array.Empty<string>(), "/mnt/media/x", out var mount));
        Assert.Null(mount);
    }

    [Fact]
    public void AMountPointWithASpaceIsUnescaped()
    {
        var lines = new[] { @"50 25 8:49 / /mnt/My\040Media rw,relatime shared:51 - ext4 /dev/sdd1 rw" };
        StorageProbe.IsReadOnlyMount(lines, "/mnt/My Media/Film", out var mount);
        Assert.Equal("/mnt/My Media", mount);
    }

    [Fact]
    public void AWritableDirectoryIsReportedWritable()
    {
        var directory = Directory.CreateTempSubdirectory("dialogueboost-probe").FullName;
        try
        {
            var status = new StorageProbe().Check(directory);

            Assert.True(status.IsWritable);
            Assert.Equal(StorageState.Writable, status.State);
            Assert.Empty(Directory.GetFileSystemEntries(directory)); // the probe file cleans up after itself
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ADirectoryThatIsNotThereIsMissingRatherThanDenied()
    {
        var status = new StorageProbe().Check(Path.Combine(Path.GetTempPath(), "dialogueboost-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(StorageState.Missing, status.State);
        Assert.Contains("not mounted", status.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkingFolderIsRememberedUntilItIsForgotten()
    {
        var now = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
        var probe = new StorageProbe(() => now, () => new List<string>());

        var directory = Directory.CreateTempSubdirectory("dialogueboost-probe").FullName;
        try
        {
            Assert.True(probe.Check(directory).IsWritable);

            Directory.Delete(directory, recursive: true);
            Assert.True(probe.Check(directory).IsWritable); // remembered, so a run does not re-probe per episode

            probe.Forget();
            Assert.Equal(StorageState.Missing, probe.Check(directory).State);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ARefusalIsMeasuredAgainEveryTime()
    {
        // A remembered refusal keeps reporting the mode it saw before the chmod that fixed it, and
        // an error message that lies about the mode is worse than no message.
        var now = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
        var probe = new StorageProbe(() => now, () => new List<string>());

        var directory = Path.Combine(Path.GetTempPath(), "dialogueboost-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(StorageState.Missing, probe.Check(directory).State);

        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(probe.Check(directory).IsWritable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheExplanationNamesThePathTheAccountAndWhatToLookAt()
    {
        var denied = new StorageStatus(StorageState.Denied, "/mnt/media/Shows/Show", "0755", "/mnt/media", 1024, "jellyfin");
        string explanation = denied.Explain();

        Assert.Contains("/mnt/media/Shows/Show", explanation, StringComparison.Ordinal);
        Assert.Contains("jellyfin", explanation, StringComparison.Ordinal);
        Assert.Contains("0755", explanation, StringComparison.Ordinal);
        Assert.Contains("STORAGE-PERMISSIONS", explanation, StringComparison.Ordinal);
    }
}
