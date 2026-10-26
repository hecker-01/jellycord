using System.Globalization;
using Jellyfin.Plugin.Jellycord.Configuration;

namespace Jellyfin.Plugin.Jellycord.Presence;

/// <summary>Turns "user X is at position P in item I" into a Discord activity.</summary>
public static class PresenceMapper
{
    private const int MaxText = 128;
    private const string PausedSuffix = " · Paused";
    private const string AssetBase = "https://raw.githubusercontent.com/hecker-01/jellycord/main/assets/";

    public const string LogoUrl = AssetBase + "logo.png";
    public const string PlayingIconUrl = AssetBase + "playing.png";
    public const string PausedIconUrl = AssetBase + "paused.png";

    /// <summary>Returns null when this item shouldn't be shown at all.</summary>
    /// <param name="artworkUrl">A public poster found by <see cref="ArtworkResolver"/>, if any.</param>
    /// <param name="serverName">Shown instead of the title for private items.</param>
    public static PresenceActivity? Map(
        MediaSnapshot media,
        long positionTicks,
        bool paused,
        long nowMs,
        PluginConfiguration cfg,
        string? artworkUrl = null,
        string serverName = "Jellyfin")
    {
        if (paused && !cfg.ShowWhenPaused)
        {
            return null;
        }

        // the per-kind toggles apply to private items too
        var kindEnabled = media.Kind switch
        {
            MediaKind.Episode => cfg.ShowEpisodes,
            MediaKind.Movie => cfg.ShowMovies,
            MediaKind.Music => cfg.ShowMusic,
            _ => false,
        };
        if (!kindEnabled)
        {
            return null;
        }

        if (media.IsPrivate)
        {
            // nothing about the item itself: no title, poster, progress, pause state or even
            // its kind (always Watching, never Listening)
            var logo = ResolveFallback(cfg);
            return new PresenceActivity
            {
                Type = PresenceActivity.Watching,
                Name = Fit(string.IsNullOrWhiteSpace(serverName) ? "Jellyfin" : serverName)!,
                LargeImage = logo,
                LargeText = logo is null ? null : Fit(serverName),
            };
        }

        int type = PresenceActivity.Watching;
        string name;
        string? details;
        string? state;

        switch (media.Kind)
        {
            case MediaKind.Episode:
                name = Coalesce(media.SeriesName, media.Title);
                details = name;
                state = EpisodeLine(media);
                break;
            case MediaKind.Movie:
                name = media.Title;
                details = media.Title;
                state = media.Year?.ToString(CultureInfo.InvariantCulture);
                break;
            case MediaKind.Music:
                type = PresenceActivity.Listening;
                name = media.Title;
                details = media.Title;
                state = media.Artists is null
                    ? media.Album
                    : media.Album is null ? $"by {media.Artists}" : $"by {media.Artists} · {media.Album}";
                break;
            default:
                return null;
        }

        if (paused)
        {
            // truncate before appending, so a long episode line can't push "Paused" off the end
            state = Fit(state, MaxText - PausedSuffix.Length) is { } s ? s + PausedSuffix : "Paused";
        }

        long? start = null, end = null;
        if (!paused)
        {
            start = nowMs - (Math.Max(0, positionTicks) / TimeSpan.TicksPerMillisecond);
            if (media.RunTimeTicks is > 0)
            {
                end = start + (media.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond);
            }

            // unknown runtime, or played past it: an elapsed timer rather than no timer or a full bar
            if (end <= nowMs)
            {
                end = null;
            }
        }

        var largeImage = ResolveImage(media, cfg, artworkUrl);
        var showBadge = cfg.ShowStatusIcon && largeImage is not null;

        return new PresenceActivity
        {
            Type = type,
            // Discord rejects an activity without a name
            Name = Fit(name) ?? "Unknown title",
            Details = Fit(details),
            State = Fit(state),
            StartMs = start,
            EndMs = end,
            LargeImage = largeImage,
            LargeText = Fit(media.Kind == MediaKind.Music ? media.Album ?? media.Title : name),
            SmallImage = showBadge ? (paused ? PausedIconUrl : PlayingIconUrl) : null,
            SmallText = showBadge ? (paused ? "Paused" : "via Jellycord") : null,
        };
    }

    internal static string? EpisodeLine(MediaSnapshot media)
    {
        var code = media.Season is not null && media.Episode is not null
            ? $"S{media.Season}E{media.Episode}"
            : media.Episode is not null ? $"E{media.Episode}" : null;
        if (code is null)
        {
            return media.Title;
        }

        return string.IsNullOrWhiteSpace(media.Title) ? code : $"{code} · {media.Title}";
    }

    internal static string? ResolveImage(MediaSnapshot media, PluginConfiguration cfg, string? artworkUrl = null)
    {
        var direct = PublicPosterUrl(media, cfg);
        if (direct is not null)
        {
            return direct;
        }

        if (!string.IsNullOrEmpty(artworkUrl))
        {
            return artworkUrl;
        }

        return ResolveFallback(cfg);
    }

    private static string? ResolveFallback(PluginConfiguration cfg)
    {
        var fallback = cfg.FallbackImage?.Trim();
        if (string.IsNullOrEmpty(fallback))
        {
            return LogoUrl;
        }

        return fallback.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : fallback;
    }

    /// <summary>The poster straight from this server, when it has a public HTTPS URL.</summary>
    public static string? PublicPosterUrl(MediaSnapshot media, PluginConfiguration cfg)
    {
        // Jellyfin's image endpoint is anonymous: never put an api_key in this URL,
        // it is visible to anyone looking at the profile.
        if (Uri.TryCreate(cfg.PublicUrl?.Trim(), UriKind.Absolute, out var baseUri)
            && baseUri.Scheme == Uri.UriSchemeHttps
            && ArtworkResolver.IsPublicHost(baseUri))
        {
            // scheme, host, port and path only: never user-info, a query or a fragment from the setting
            var root = baseUri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            var id = media.ImageItemId == Guid.Empty ? media.ItemId : media.ImageItemId;
            var url = $"{root.TrimEnd('/')}/Items/{id:N}/Images/Primary?fillHeight=512&quality=90";
            if (url.Length <= 300)
            {
                return url;
            }
        }

        return null;
    }

    private static string Coalesce(string? a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    /// <summary>Discord wants 2..128 chars on one line.</summary>
    internal static string? Fit(string? s, int max = MaxText)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        // newlines, tabs and other control characters become spaces
        s = string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                span[i] = char.IsControl(src[i]) ? ' ' : src[i];
            }
        }).Trim();

        if (s.Length > max)
        {
            // never split a surrogate pair (emoji, rare CJK): a lone half is invalid UTF-16
            var keep = max - 1;
            if (char.IsHighSurrogate(s[keep - 1]))
            {
                keep--;
            }

            s = string.Concat(s.AsSpan(0, keep), "…");
        }

        return s.Length < 2 ? s.PadRight(2, '⠀') : s;
    }
}
