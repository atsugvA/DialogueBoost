using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.DialogueBoost.Output;

/// <summary>
/// Answers whether a sidecar can actually be written next to a source file, before anything is
/// encoded.
/// </summary>
/// <remarks>
/// The probe writes a file, because nothing else is the truth: a mode check passes where an ACL
/// denies, and an ACL check passes where a read-only mount denies. It probes **the item's own
/// directory** rather than the volume — on this project's ntfs3 media volume every release folder
/// carries its own Linux mode, so a volume-level check passes while the one folder that matters is
/// denied, which is exactly how the failure stayed intermittent for weeks.
/// </remarks>
public sealed class StorageProbe
{
    /// <summary>
    /// How long an answer stands. Long enough that a run does not re-probe the same folder per
    /// episode, short enough that fixing the permissions does not need a restart.
    /// </summary>
    private static readonly TimeSpan Remembers = TimeSpan.FromSeconds(60);

    /// <summary>Below this, a write failure is a full disk rather than a permission problem.</summary>
    private const long NoSpaceBelowBytes = 64L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, (DateTime At, StorageStatus Status)> _seen = new(StringComparer.Ordinal);

    private readonly Func<DateTime> _now;
    private readonly Func<IEnumerable<string>> _mounts;

    public StorageProbe()
        : this(() => DateTime.UtcNow, ReadMountInfo)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageProbe"/> class with its two sources of
    /// outside information injected, so the parsing can be tested without a filesystem.
    /// </summary>
    public StorageProbe(Func<DateTime> now, Func<IEnumerable<string>> mounts)
    {
        _now = now;
        _mounts = mounts;
    }

    /// <summary>
    /// Probes a directory, reusing a recent answer only where that answer was "yes".
    /// </summary>
    public StorageStatus Check(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new StorageStatus(StorageState.Missing, directory ?? string.Empty, null, null, null, ServiceUser);
        }

        if (_seen.TryGetValue(directory, out var remembered) && _now() - remembered.At < Remembers)
        {
            return remembered.Status;
        }

        var status = Probe(directory);

        // Only a working folder is remembered. A refusal carries facts that go stale the moment
        // somebody fixes them — a remembered "mode is 0700" after a chmod is a message that lies —
        // and re-probing a folder that refuses is cheap, so it is measured every time.
        if (status.IsWritable)
        {
            _seen[directory] = (_now(), status);
        }

        return status;
    }

    /// <summary>
    /// Forgets what was learned, so the next check probes again. Used when a run starts, and by the
    /// page's own storage check — an operator who has just fixed the permissions should not have to
    /// wait out a cache.
    /// </summary>
    public void Forget() => _seen.Clear();

    /// <summary>
    /// The account Jellyfin — and therefore this plugin — writes as. Half of every permission
    /// answer, and never mentioned by the errors this replaces.
    /// </summary>
    public static string ServiceUser => Environment.UserName;

    /// <summary>
    /// Whether the filesystem holding a path is mounted read-only, or <c>null</c> where that cannot
    /// be told. Pure, so the parsing is tested rather than trusted.
    /// </summary>
    /// <param name="mountInfoLines">Lines in the format of Linux's <c>/proc/self/mountinfo</c>.</param>
    /// <param name="path">The path to locate.</param>
    /// <param name="mountPoint">The mount point the path belongs to.</param>
    public static bool? IsReadOnlyMount(IEnumerable<string> mountInfoLines, string path, out string? mountPoint)
    {
        mountPoint = null;
        bool? readOnly = null;
        int longest = -1;

        foreach (var line in mountInfoLines)
        {
            // "36 25 8:1 / /mnt/Data rw,relatime shared:1 - ntfs3 /dev/sda1 rw,uid=1000"
            var fields = line.Split(' ');
            if (fields.Length < 6)
            {
                continue;
            }

            string point = Unescape(fields[4]);
            if (!Covers(point, path) || point.Length <= longest)
            {
                continue;
            }

            longest = point.Length;
            mountPoint = point;
            readOnly = fields[5].Split(',').Contains("ro");
        }

        return readOnly;
    }

    private StorageStatus Probe(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Describe(StorageState.Missing, directory);
        }

        // Written, not inspected: a mode says nothing about ACLs, and an ACL says nothing about a
        // read-only mount. The file is deleted on close, and never touches the source media.
        string probe = Path.Combine(directory, $".dialogueboost-write-probe-{Guid.NewGuid():N}");

        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            return Describe(StorageState.Writable, directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Describe(null, directory);
        }
    }

    /// <summary>
    /// Everything known about a directory. A refused write becomes one of the three refusals here —
    /// the distinction .NET does not make.
    /// </summary>
    /// <param name="known">The state, where the probe already knows it; <c>null</c> after a refusal.</param>
    /// <param name="directory">The directory probed.</param>
    private StorageStatus Describe(StorageState? known, string directory)
    {
        bool? readOnly = IsReadOnlyMount(_mounts(), directory, out string? mountPoint);
        long? free = FreeBytes(directory);

        var state = known ?? (free is long bytes && bytes < NoSpaceBelowBytes
            ? StorageState.NoSpace
            : readOnly == true ? StorageState.ReadOnlyMount : StorageState.Denied);

        return new StorageStatus(state, directory, Mode(directory), mountPoint, free, ServiceUser);
    }

    private static string? Mode(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var mode = File.GetUnixFileMode(directory);
            return "0" + Convert.ToString((int)mode & 0b111_111_111, 8).PadLeft(3, '0');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static long? FreeBytes(string directory)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadMountInfo()
    {
        // Linux only, and only ever used to make a message more precise: everywhere else the probe
        // still tells writable from not, it just cannot name read-only as its own case.
        try
        {
            return OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo")
                ? File.ReadAllLines("/proc/self/mountinfo")
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Whether a mount point contains a path — by path segment, so /mnt/Data does not "contain"
    /// /mnt/Database.
    /// </summary>
    private static bool Covers(string mountPoint, string path)
    {
        if (mountPoint == "/" || string.Equals(mountPoint, path, StringComparison.Ordinal))
        {
            return true;
        }

        string prefix = mountPoint.EndsWith('/') ? mountPoint : mountPoint + '/';
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// mountinfo escapes space, tab, newline and backslash as octal.
    /// </summary>
    private static string Unescape(string field) => field
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
}
