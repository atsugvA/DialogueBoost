namespace Jellyfin.Plugin.DialogueBoost.Configuration;

/// <summary>
/// What happens when a whole library is added to Jellyfin after the selection was last saved.
/// </summary>
/// <remarks>
/// A new library is not the same case as new media inside a chosen one: nothing contains it, so no
/// scope can cover it however the scopes are resolved. It is a decision, and this is where the user
/// makes it once.
/// </remarks>
public enum NewLibraryPolicy
{
    /// <summary>
    /// Leave it out and say so — the config page badges it as new and not included, and the user
    /// decides. Nothing is processed behind their back.
    /// </summary>
    FlagOnly = 0,

    /// <summary>
    /// Cover it like the rest: the library is added to the selection on the next run, and its
    /// contents are processed from then on.
    /// </summary>
    AutoInclude = 1
}
