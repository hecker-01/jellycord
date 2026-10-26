using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Jellycord.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycord.Presence;

/// <summary>
/// Finds a poster URL Discord can fetch when the Jellyfin server itself isn't reachable
/// from the internet (LAN, Tailscale, VPN). In order:
///   1. the image's original remote URL, if Jellyfin kept it;
///   2. Jellyfin's own metadata providers (TMDB, MusicBrainz/Cover Art Archive, TheAudioDB…),
///      using the server's existing provider setup, so no extra API keys;
///   3. (opt-in) uploading the local poster to a temporary public host (Litterbox, 72h).
/// Results are cached per image item.
/// </summary>
public sealed partial class ArtworkResolver
{
    private const int MaxCacheEntries = 2000;
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan UploadTtl = TimeSpan.FromHours(70); // Litterbox keeps files 72h
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ErrorTtl = TimeSpan.FromMinutes(2); // a failed lookup may just be a blip
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArtworkResolver> _logger;
    private readonly ConcurrentDictionary<Guid, Entry> _cache = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<string?>>> _inflight = new();

    public ArtworkResolver(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IHttpClientFactory httpClientFactory,
        ILogger<ArtworkResolver> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Returns a public poster URL for the item, or null to use the configured fallback.</summary>
    public Task<string?> ResolveAsync(MediaSnapshot media, PluginConfiguration cfg)
    {
        // a public server URL makes all of this unnecessary
        if (PresenceMapper.PublicPosterUrl(media, cfg) is not null)
        {
            return Task.FromResult<string?>(null);
        }

        var key = media.ImageItemId == Guid.Empty ? media.ItemId : media.ImageItemId;
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow
            && (hit.Url is not null || !cfg.UploadArtwork || hit.UploadTried))
        {
            return Task.FromResult(hit.Url);
        }

        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<string?>>(() => LookupAndCacheAsync(k, cfg)));
        return lazy.Value;
    }

    private async Task<string?> LookupAndCacheAsync(Guid itemId, PluginConfiguration cfg)
    {
        try
        {
            var (url, ttl, uploadTried) = await LookupAsync(itemId, cfg).ConfigureAwait(false);
            Store(itemId, new Entry(url, DateTimeOffset.UtcNow + ttl, uploadTried));
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Artwork lookup for {Item} failed", itemId);
            Store(itemId, new Entry(null, DateTimeOffset.UtcNow + ErrorTtl, cfg.UploadArtwork));
            return null;
        }
        finally
        {
            _inflight.TryRemove(itemId, out _);
        }
    }

    /// <summary>
    /// When full, drops expired entries, then the ones expiring soonest. Uploads live longest, so they
    /// are the last to go (re-uploading means another copy of the poster on a public host).
    /// </summary>
    private void Store(Guid itemId, Entry entry)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            var now = DateTimeOffset.UtcNow;
            var victims = _cache.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList();
            if (_cache.Count - victims.Count >= MaxCacheEntries)
            {
                victims.AddRange(_cache
                    .Where(kv => kv.Value.Expires > now)
                    .OrderBy(kv => kv.Value.Expires)
                    .Take(MaxCacheEntries / 10)
                    .Select(kv => kv.Key));
            }

            foreach (var key in victims)
            {
                _cache.TryRemove(key, out _);
            }
        }

        _cache[itemId] = entry;
    }

    private async Task<(string? Url, TimeSpan Ttl, bool UploadTried)> LookupAsync(Guid itemId, PluginConfiguration cfg)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return (null, NegativeTtl, false);
        }

        // 1. Jellyfin sometimes stores the remote URL rather than a local copy
        var primary = item.GetImageInfo(ImageType.Primary, 0);
        if (primary is not null && IsPublicHttps(primary.Path))
        {
            return (Shrink(primary.Path), PositiveTtl, false);
        }

        // 2. ask the server's metadata providers
        try
        {
            using var cts = new CancellationTokenSource(LookupTimeout);
            var ct = cts.Token;
            var remote = await _providerManager.GetAvailableRemoteImages(
                item,
                new RemoteImageQuery(string.Empty) { ImageType = ImageType.Primary, IncludeAllLanguages = false },
                ct).ConfigureAwait(false);
            // prefer the highest-rated image, which is what Jellyfin itself picks by default
            var best = remote
                .Where(r => IsPublicHttps(r.Url))
                .OrderByDescending(r => r.CommunityRating ?? 0)
                .ThenByDescending(r => r.VoteCount ?? 0)
                .FirstOrDefault();
            if (best is not null)
            {
                _logger.LogDebug("Artwork for {Name} from {Provider}", item.Name, best.ProviderName);
                return (Shrink(best.Url), PositiveTtl, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Remote image lookup failed for {Name}", item.Name);
        }

        // 3. opt-in: publish the local poster temporarily
        if (cfg.UploadArtwork && primary is not null && primary.IsLocalFile && File.Exists(primary.Path))
        {
            using var cts = new CancellationTokenSource(UploadTimeout);
            var uploaded = await UploadAsync(primary.Path, cts.Token).ConfigureAwait(false);
            return (uploaded, uploaded is null ? NegativeTtl : UploadTtl, true);
        }

        return (null, NegativeTtl, cfg.UploadArtwork);
    }

    private async Task<string?> UploadAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length > 10 * 1024 * 1024)
        {
            return null;
        }

        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        // PHP's multipart parser wants quoted names, no filename*=, and an unquoted boundary;
        // .NET's defaults do the opposite, so build the headers by hand.
        var boundary = "jellycord" + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent(boundary);
        form.Headers.Remove("Content-Type");
        form.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
        form.Add(FormField("reqtype", "fileupload"));
        form.Add(FormField("time", "72h"));

        await using var stream = File.OpenRead(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var file = new StreamContent(stream);
        file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"fileToUpload\"; filename=\"poster{ext}\"");
        file.Headers.ContentType = new MediaTypeHeaderValue(ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        });
        form.Add(file);

        using var res = await client.PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", form, ct).ConfigureAwait(false);
        var body = (await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        if (res.IsSuccessStatusCode && IsPublicHttps(body))
        {
            _logger.LogInformation("Uploaded poster to {Url} (expires in 72h)", body);
            return body;
        }

        _logger.LogWarning("Poster upload failed: {Status} {Body}", (int)res.StatusCode, body.Length > 200 ? body[..200] : body);
        return null;
    }

    private static StringContent FormField(string name, string value)
    {
        var content = new StringContent(value);
        content.Headers.Clear();
        content.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{name}\"");
        return content;
    }

    /// <summary>
    /// An HTTPS URL Discord can fetch and anyone may see: no credentials, no API key, and not a
    /// LAN, link-local, CGNAT/Tailscale or other private address (Discord couldn't reach those anyway).
    /// </summary>
    internal static bool IsPublicHttps(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && u.Scheme == Uri.UriSchemeHttps
           && url!.Length <= 300
           && string.IsNullOrEmpty(u.UserInfo)
           && !HasSecretQuery(u.Query)
           && IsPublicHost(u);

    internal static bool IsPublicHost(Uri u)
    {
        if (u.IsLoopback)
        {
            return false;
        }

        if (u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return IPAddress.TryParse(u.DnsSafeHost, out var ip) && IsPublicAddress(ip);
        }

        var host = u.DnsSafeHost.TrimEnd('.');
        return host.Contains('.', StringComparison.Ordinal)
               && !PrivateSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] PrivateSuffixes = [".local", ".lan", ".internal", ".home.arpa", ".localdomain"];

    private static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // link-local, site-local and unique-local (fc00::/7)
            return !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6UniqueLocal && !IPAddress.IsLoopback(ip);
        }

        var b = ip.GetAddressBytes();
        return !(b[0] == 10
                 || b[0] == 127
                 || b[0] == 0
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)); // CGNAT, used by Tailscale
    }

    private static bool HasSecretQuery(string query)
        => query.Contains("api_key", StringComparison.OrdinalIgnoreCase)
           || query.Contains("apikey", StringComparison.OrdinalIgnoreCase)
           || query.Contains("token", StringComparison.OrdinalIgnoreCase);

    /// <summary>TMDB "original" images can be several MB; Discord only needs a small square.</summary>
    internal static string Shrink(string url) => TmdbOriginal().Replace(url, "/t/p/w500/");

    [GeneratedRegex(@"/t/p/original/", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbOriginal();

    private sealed record Entry(string? Url, DateTimeOffset Expires, bool UploadTried);
}
