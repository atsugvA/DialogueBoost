using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DialogueBoost.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DialogueBoost;

/// <summary>
/// Dialogue Boost plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public override string Name => "Dialogue Boost";

    public override Guid Id => Guid.Parse("a818318e-49b0-466d-963a-4467c6999b10");

    public override string Description => "Generates dialogue-enhanced sidecar audio tracks for Jellyfin media libraries without touching source video files.";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Dialogue Boost",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.Web.config.html",
                EnableInMainMenu = true
            }
        };
    }
}
