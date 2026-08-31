namespace Jellyfin.Plugin.DialogueBoost.Configuration;

/// <summary>
/// Whose viewing history decides that an item has been watched.
/// </summary>
/// <remarks>
/// It decides two things: whether a sidecar is worth making (<c>SkipWatchedItems</c>), and whether
/// one already made can be deleted (the cleanup task). Both are the same question — does anybody
/// still need this — and on a server with more than one account the answer is not "did anyone
/// finish it". It used to be: one account marking an episode played deleted the boosted track the
/// other accounts were still using.
/// </remarks>
public enum WatchedByPolicy
{
    /// <summary>
    /// Every account the server has, minus the disabled ones. Safe rather than useful: an account
    /// that never watches anything — an automation login, a dormant guest — holds every item
    /// unwatched forever, which costs re-encoding but never deletes something in use. It is the
    /// default because it cannot lose anyone's sidecar, and the point is to replace it with
    /// <see cref="ChosenAccounts"/> once the accounts on this server are known.
    /// </summary>
    EveryActiveAccount = 0,

    /// <summary>
    /// The accounts named in <c>WatchedByUserIds</c>, and only those. An item is watched when every
    /// one of them has played it. Choosing none means no account's history counts, so nothing is
    /// ever skipped or cleaned up.
    /// </summary>
    ChosenAccounts = 1
}
