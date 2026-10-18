using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellycord.Configuration;

/// <summary>
/// Admin settings. Everything here is readable by the dashboard, so secrets
/// (client secret, user tokens) live in <see cref="Discord.CredentialStore"/> instead.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Discord Application ID (each server admin brings their own app).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Public base URL of this Jellyfin server, e.g. https://jellyfin.example.com.
    /// Used for poster art; Discord's image proxy must be able to reach it.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// Image shown when no poster is available: an https URL or an asset key uploaded to the
    /// Discord app. Empty uses the Jellycord logo; "none" shows no image.
    /// </summary>
    public string FallbackImage { get; set; } = string.Empty;

    /// <summary>
    /// Last-resort poster source for private servers: upload the local poster to Litterbox
    /// (catbox.moe), a public temporary host that deletes files after 72 hours. Off by default
    /// because it publishes your artwork to a third party.
    /// </summary>
    public bool UploadArtwork { get; set; }

    /// <summary>Libraries whose items are private: only "Watching {server}" is shown.</summary>
    public string[] PrivateLibraryIds { get; set; } = [];

    /// <summary>Comma-separated tags that make an item private (also inherited from series/albums).</summary>
    public string PrivateTags { get; set; } = "nsfw, adult, xxx, hentai, porn";

    /// <summary>Comma-separated parental ratings that make an item private.</summary>
    public string PrivateRatings { get; set; } = "XXX, X, AO, NC-17, R18, R18+, 18+";

    /// <summary>Add "Discord status" to jellyfin-web's user menu (needs the File Transformation plugin).</summary>
    public bool InjectWebUi { get; set; } = true;

    /// <summary>Small playing/paused badge on the corner of the poster.</summary>
    public bool ShowStatusIcon { get; set; } = true;

    public bool ShowMovies { get; set; } = true;

    public bool ShowEpisodes { get; set; } = true;

    public bool ShowMusic { get; set; } = true;

    /// <summary>Keep the status (with "paused") while paused; otherwise clear it.</summary>
    public bool ShowWhenPaused { get; set; } = true;

    /// <summary>Wait this long after playback stops before clearing, so autoplay doesn't flicker.</summary>
    public int ClearDelaySeconds { get; set; } = 5;
}
