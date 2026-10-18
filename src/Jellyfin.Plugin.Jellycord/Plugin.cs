using System.Globalization;
using Jellyfin.Plugin.Jellycord.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jellycord;

/// <summary>Jellycord: mirrors Jellyfin playback to each linked user's Discord status.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "92be543f-9182-4590-8b3a-7cdab36f3ef1";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Jellycord";

    public override string Description =>
        "Shows what you're watching on Jellyfin as your Discord status. No Discord desktop client needed.";

    public override Guid Id => Guid.Parse(PluginGuid);

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture, "{0}.Web.configPage.html", GetType().Namespace),
            },
        ];
    }
}
