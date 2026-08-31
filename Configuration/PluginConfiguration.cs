using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DialogueBoost.Configuration.Profiles;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DialogueBoost.Configuration;

/// <summary>
/// Plugin configuration model.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the library nodes an older version of the plugin stored here.
    /// </summary>
    /// <remarks>
    /// <b>Legacy — read once, never written.</b> The selection lives in its own document now
    /// (<c>SelectionStore</c>, served by <c>GET/PUT /Plugins/DialogueBoost/Selection</c>), because
    /// storing it here meant every save rewrote the whole configuration and any two writers could
    /// silently lose one another's work. This property survives only so that a selection made
    /// before the upgrade is imported on the next start; nothing reads it afterwards.
    /// </remarks>
    public List<Guid> SelectedLibraries { get; set; } = new();

    private int _maxConcurrentJobs = 2;

    /// <summary>
    /// Gets or sets how many items may be encoded at once. Default is 2: enough to keep a disk
    /// busy while one job waits on I/O, low enough that the first run on a NAS or a single-board
    /// server does not saturate every core. This is the only lever over a run's cost — ffmpeg's
    /// own <c>-threads</c> changes nothing measurable, because each audio codec is single-threaded
    /// per stream.
    /// </summary>
    public int MaxConcurrentJobs
    {
        get => _maxConcurrentJobs;
        set => _maxConcurrentJobs = Math.Clamp(value, 1, 32);
    }

    /// <summary>
    /// Gets or sets whether encoding holds off while somebody is streaming. Default is true: a
    /// server that stutters someone's film the first night the plugin is installed is a worse
    /// first impression than a run that finishes later.
    /// </summary>
    public bool PauseDuringActivePlayback { get; set; } = true;

    /// <summary>
    /// Gets or sets what happens when media appears in a selected scope while the server is running.
    /// Default is <see cref="Configuration.NewMediaTrigger.WhenSettled"/> — arrivals are covered
    /// once they stop arriving, rather than waiting for the next scheduled run.
    /// </summary>
    public NewMediaTrigger NewMediaTrigger { get; set; } = NewMediaTrigger.WhenSettled;

    private int _newMediaSettleMinutes = 10;

    /// <summary>
    /// Gets or sets what happens when a whole library is added to Jellyfin after the selection was
    /// last saved. Default is <see cref="Configuration.NewLibraryPolicy.FlagOnly"/> — it is shown as
    /// new and not included, and nothing is processed without being asked for.
    /// </summary>
    public NewLibraryPolicy NewLibraryPolicy { get; set; } = NewLibraryPolicy.FlagOnly;

    /// <summary>
    /// Gets or sets how long arrivals must stay quiet before a batch counts as complete, in minutes.
    /// Default is 10: long enough for a download's files and Jellyfin's own scan of them to finish
    /// arriving, short enough that an evening's downloads are covered the same evening.
    /// </summary>
    public int NewMediaSettleMinutes
    {
        get => _newMediaSettleMinutes;
        set => _newMediaSettleMinutes = Math.Clamp(value, 1, 720);
    }

    /// <summary>
    /// Gets or sets a value indicating whether to skip creating sidecars for items that are already marked as watched.
    /// Default is true.
    /// </summary>
    public bool SkipWatchedItems { get; set; } = true;

    /// <summary>
    /// Gets or sets whose viewing history decides that an item has been watched. Default is
    /// <see cref="Configuration.WatchedByPolicy.ChosenAccounts"/> with nothing chosen, which makes
    /// a fresh install inert in both directions: no account counts, so
    /// <see cref="Processing.WatchedItems.IsWatched"/> is false for everything — nothing is skipped
    /// for being watched, and the cleanup deletes nothing. Skipping and deleting are opted into by
    /// naming the accounts, which is the half that cannot be undone by re-running.
    /// </summary>
    public WatchedByPolicy WatchedBy { get; set; } = WatchedByPolicy.ChosenAccounts;

    /// <summary>
    /// Gets or sets the accounts whose history counts, when <see cref="WatchedBy"/> is
    /// <see cref="Configuration.WatchedByPolicy.ChosenAccounts"/>. Ignored otherwise.
    /// </summary>
    public List<Guid> WatchedByUserIds { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether to include manually created (exempt) files when cleaning up watched items.
    /// Default is false.
    /// </summary>
    public bool IncludeExemptInCleanup { get; set; } = false;

    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Gets or sets where tracks are written. Default is
    /// <see cref="Configuration.SidecarLocation.MetadataFolder"/>, Jellyfin's own folder for each
    /// item, which nothing else on the machine looks into: a track beside the media is adopted by
    /// library managers such as Radarr and Sonarr as one of the video's extras, and renamed, moved
    /// and deleted with it. A configuration saved before this setting existed has no element for
    /// it and so takes the default, and its next run moves the tracks it already has rather than
    /// encoding them again.
    /// </summary>
    public SidecarLocation SidecarLocation { get; set; } = SidecarLocation.MetadataFolder;

    public TrackSelectionRulesConfig TrackSelectionRules { get; set; } = new();

    public DialogueBoostProfile DialogueBoostProfile { get; set; } = new();

    public NightModeProfile NightModeProfile { get; set; } = new();

    public SpeechProfile SpeechProfile { get; set; } = new();

    public Ebur128Profile Ebur128Profile { get; set; } = new();

    public CustomProfile CustomProfile { get; set; } = new();

    /// <summary>
    /// Gets all profiles defined in the plugin configuration.
    /// </summary>
    public IEnumerable<BaseProfileConfig> GetAllProfiles()
    {
        yield return DialogueBoostProfile;
        yield return NightModeProfile;
        yield return SpeechProfile;
        yield return Ebur128Profile;
        yield return CustomProfile;
    }

    /// <summary>
    /// Gets a profile by its unique ID.
    /// </summary>
    public BaseProfileConfig? GetProfile(string profileId)
    {
        return GetAllProfiles().FirstOrDefault(p => p.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether this profile is the one whose track playback starts on.
    /// </summary>
    /// <remarks>
    /// At most one may be, whatever the switches say. Two sidecars both claiming it is not a tie
    /// the plugin gets to break: Jellyfin lists every external stream and takes the lowest index,
    /// so the winner is whichever filename happens to sort first. Resolved here instead, once,
    /// in the order the profiles are declared — the page offers the switch as a single choice, and
    /// this is what makes that true of a configuration written any other way.
    /// </remarks>
    public bool ClaimsDefaultTrack(BaseProfileConfig profile) =>
        profile.Enabled
        && profile.SetAsDefaultTrack
        && string.Equals(
            GetAllProfiles().FirstOrDefault(p => p.Enabled && p.SetAsDefaultTrack)?.Id,
            profile.Id,
            StringComparison.OrdinalIgnoreCase);
}
