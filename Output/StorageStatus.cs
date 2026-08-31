using System;
using System.Globalization;

namespace Jellyfin.Plugin.DialogueBoost.Output;

/// <summary>
/// Why a directory can or cannot take a sidecar.
/// </summary>
/// <remarks>
/// .NET reports "not mounted", "mounted read-only" and "this directory's mode denies the service
/// user" identically — an <see cref="UnauthorizedAccessException"/> with no detail — and that is
/// why the failure stayed mysterious for so long. These are the three the probe separates.
/// </remarks>
public enum StorageState
{
    /// <summary>A file was created there and removed again.</summary>
    Writable,

    /// <summary>The directory is not there at all — most often a volume that is not mounted.</summary>
    Missing,

    /// <summary>The whole filesystem is mounted read-only, so no directory on it can be written.</summary>
    ReadOnlyMount,

    /// <summary>The filesystem is writable; this directory's own permissions are not.</summary>
    Denied,

    /// <summary>The volume is out of space.</summary>
    NoSpace
}

/// <summary>
/// One directory's writability, with the facts needed to fix it.
/// </summary>
/// <param name="State">What the probe found.</param>
/// <param name="Directory">The directory that was probed — the item's own, never the volume.</param>
/// <param name="Mode">The directory's Unix mode, where the platform has one.</param>
/// <param name="MountPoint">The filesystem the directory is on, where it could be determined.</param>
/// <param name="FreeBytes">Space left on that filesystem, or <c>null</c> if it could not be read.</param>
/// <param name="ServiceUser">The account Jellyfin is running as — half of every permission answer.</param>
public sealed record StorageStatus(
    StorageState State,
    string Directory,
    string? Mode,
    string? MountPoint,
    long? FreeBytes,
    string ServiceUser)
{
    /// <summary>
    /// Gets a value indicating whether a sidecar can be written here.
    /// </summary>
    public bool IsWritable => State == StorageState.Writable;

    /// <summary>
    /// The failure in one sentence that names the path, the account and what to look at — the thing
    /// a generic ffmpeg error never said.
    /// </summary>
    public string Explain() => State switch
    {
        StorageState.Writable => $"'{Directory}' is writable by '{ServiceUser}'.",

        StorageState.Missing =>
            $"'{Directory}' does not exist. If it is on a separate volume, that volume is not mounted.",

        StorageState.ReadOnlyMount =>
            $"'{Directory}' is on '{MountPoint}', which is mounted read-only. Nothing can be written there until it is remounted read-write.",

        StorageState.NoSpace =>
            $"'{Directory}' is on '{MountPoint}', which has {Space} left.",

        _ =>
            $"'{Directory}' denies writes to '{ServiceUser}'{ModePhrase}. The filesystem is writable, so this is the directory's own permissions — " +
            "on an ntfs3 volume each folder carries its own Linux mode and no mount option overrides it (docs/STORAGE-PERMISSIONS.md)."
    };

    private string ModePhrase => Mode is null ? string.Empty : $", and its mode is {Mode}";

    private string Space => FreeBytes is long bytes
        ? (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB"
        : "no space";
}
