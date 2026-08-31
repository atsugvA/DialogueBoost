using System.Linq;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.DialogueBoost.Integration;

/// <summary>
/// Whether anybody is watching something right now, for the setting that holds encoding back while
/// they are. The plugin's only contact with <see cref="ISessionManager"/>.
/// </summary>
public interface IPlaybackSessions
{
    /// <summary>True while at least one session is playing and not paused.</summary>
    bool AnyPlaying { get; }
}

/// <inheritdoc />
public sealed class JellyfinPlaybackSessions : IPlaybackSessions
{
    private readonly ISessionManager _sessionManager;

    public JellyfinPlaybackSessions(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public bool AnyPlaying
    {
        get
        {
            try
            {
                return _sessionManager.Sessions
                    .Any(session => session.NowPlayingItem is not null && session.PlayState?.IsPaused == false);
            }
            catch
            {
                // Unknown is "nobody": the setting exists to be polite about CPU, and a plugin that
                // stops encoding forever because it cannot read the session list is worse.
                return false;
            }
        }
    }
}
