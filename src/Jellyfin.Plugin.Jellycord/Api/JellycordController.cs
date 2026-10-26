using System.Collections.Concurrent;
using Jellyfin.Plugin.Jellycord.Discord;
using Jellyfin.Plugin.Jellycord.Playback;
using Jellyfin.Plugin.Jellycord.Presence;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycord.Api;

/// <summary>
/// Self-service linking for any signed-in user (own account only) plus admin endpoints.
/// Linking uses Discord's OAuth device flow, so Discord never needs to reach this server.
/// </summary>
[ApiController]
[Route("Jellycord")]
[Authorize]
public class JellycordController : ControllerBase
{
    internal const int MaxPrivateTagsLength = 1000;

    /// <summary>
    /// Budget for work that must finish once Discord has handed out tokens (saving a link, revoking
    /// on unlink). It replaces the request token so a closed browser tab can't abandon it halfway.
    /// </summary>
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<Guid, PendingLink> Pending = new();

    private readonly DiscordClient _discord;
    private readonly HeadlessSessionSink _sink;
    private readonly PlaybackTracker _tracker;
    private readonly CredentialStore _store;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ArtworkResolver _artwork;
    private readonly PrivacyFilter _privacy;
    private readonly MediaBrowser.Controller.IServerApplicationHost _host;
    private readonly ILogger<JellycordController> _logger;

    public JellycordController(
        DiscordClient discord,
        HeadlessSessionSink sink,
        PlaybackTracker tracker,
        CredentialStore store,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ArtworkResolver artwork,
        PrivacyFilter privacy,
        MediaBrowser.Controller.IServerApplicationHost host,
        ILogger<JellycordController> logger)
    {
        _discord = discord;
        _sink = sink;
        _tracker = tracker;
        _store = store;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _artwork = artwork;
        _privacy = privacy;
        _host = host;
        _logger = logger;
    }

    // ---------------------------------------------------------------- self-service

    /// <summary>Standalone page any user can open to link their own Discord.</summary>
    [HttpGet("Link")]
    [AllowAnonymous]
    public ActionResult GetLinkPage()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Jellycord.Web.linkPage.html");
        return stream is null ? NotFound() : File(stream, "text/html; charset=utf-8");
    }

    /// <summary>Script File Transformation injects into jellyfin-web (user menu entry + native page).</summary>
    [HttpGet("web.js")]
    [AllowAnonymous]
    public ActionResult GetWebScript()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Jellycord.Web.web.js");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        return File(stream, "application/javascript; charset=utf-8");
    }

    [HttpGet("Me")]
    public ActionResult GetMe()
    {
        if (CallerId() is not { } id)
        {
            return Unauthorized();
        }

        var account = _store.Find(id);
        return Ok(new
        {
            configured = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ClientId),
            linked = account is not null,
            needsRelink = account is not null && NeedsRelink(account),
            discordName = account?.DiscordName,
        });
    }

    [HttpPost("Me/Link")]
    public async Task<ActionResult> StartLink(CancellationToken ct)
    {
        if (CallerId() is not { } id)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ClientId))
        {
            return BadRequest(new { error = "The server admin hasn't set up a Discord application yet." });
        }

        // reuse a live code rather than minting new ones on every click
        if (Pending.TryGetValue(id, out var live) && DateTimeOffset.UtcNow < live.ExpiresAt)
        {
            return Ok(live.ToDto());
        }

        var res = await _discord.DeviceAuthorizeAsync(ct).ConfigureAwait(false);
        var body = res.Body;
        var deviceCode = body.Str("device_code");
        var userCode = body.Str("user_code");
        var verificationUri = body.Str("verification_uri_complete") ?? body.Str("verification_uri");
        if (res.Status != 200 || deviceCode is null || userCode is null || verificationUri is null)
        {
            _logger.LogWarning("Discord device authorize failed: {Status} {Body}", res.Status, res.Body?.ToJsonString());
            var hint = res.Status == 401 ? " Turn on 'Public Client' in the Discord app's OAuth2 settings, or set the client secret." : string.Empty;
            return StatusCode(502, new { error = $"Discord rejected the link request ({res.Status}).{hint}" });
        }

        var pending = new PendingLink
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            VerificationUri = verificationUri,
            IntervalSeconds = body.Num("interval") ?? 5,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(body.Num("expires_in") ?? 300),
        };
        Pending[id] = pending;
        return Ok(pending.ToDto());
    }

    [HttpGet("Me/Link")]
    public async Task<ActionResult> PollLink(CancellationToken ct)
    {
        if (CallerId() is not { } id)
        {
            return Unauthorized();
        }

        if (!Pending.TryGetValue(id, out var pending))
        {
            return Ok(new { status = _store.Find(id) is null ? "none" : "linked", discordName = _store.Find(id)?.DiscordName });
        }

        if (DateTimeOffset.UtcNow > pending.ExpiresAt)
        {
            Pending.TryRemove(id, out _);
            return Ok(new { status = "expired" });
        }

        await pending.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Pending.ContainsKey(id))
            {
                // another poll finished this link while we waited: report what it ended in
                var done = _store.Find(id);
                return Ok(new { status = done is null ? "failed" : "linked", discordName = done?.DiscordName });
            }

            // respect Discord's polling interval
            if ((DateTimeOffset.UtcNow - pending.LastPoll).TotalSeconds < pending.IntervalSeconds)
            {
                return Ok(new { status = "pending" });
            }

            pending.LastPoll = DateTimeOffset.UtcNow;
            var res = await _discord.DeviceTokenAsync(pending.DeviceCode, ct).ConfigureAwait(false);
            var account = new LinkedAccount { UserId = id };
            if (res.Status == 200 && DiscordClient.TryApplyTokens(account, res.Body))
            {
                try
                {
                    return await CompleteLinkAsync(account).ConfigureAwait(false);
                }
                finally
                {
                    // the device code is spent either way
                    Pending.TryRemove(id, out _);
                }
            }

            if (res.Status == 0)
            {
                // network trouble reaching Discord: keep the code and let the page poll again
                _logger.LogDebug("Polling Discord for a link failed: {Error}", res.Body.Str("network_error"));
                return Ok(new { status = "pending" });
            }

            switch (res.Error)
            {
                case "authorization_pending":
                    return Ok(new { status = "pending" });
                case "slow_down":
                    pending.IntervalSeconds += 5;
                    return Ok(new { status = "pending" });
                default:
                    Pending.TryRemove(id, out _);
                    _logger.LogWarning("Discord device link failed: {Status} {Error}", res.Status, res.Error);
                    return Ok(new { status = "failed", error = res.Error ?? $"HTTP {res.Status}" });
            }
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    /// <summary>The caller's privacy settings, plus the admin's rules so the page can show them.</summary>
    [HttpGet("Me/Settings")]
    public ActionResult GetMySettings()
    {
        if (CallerId() is not { } id)
        {
            return Unauthorized();
        }

        var cfg = Plugin.Instance!.Configuration;
        return Ok(new
        {
            settings = _store.GetSettings(id),
            admin = new { privateLibraryIds = cfg.PrivateLibraryIds, privateTags = cfg.PrivateTags, privateRatings = cfg.PrivateRatings },
            serverName = _host.FriendlyName,
        });
    }

    [HttpPost("Me/Settings")]
    public ActionResult SetMySettings([FromBody] UserSettings settings)
    {
        if (CallerId() is not { } id)
        {
            return Unauthorized();
        }

        // reject rather than trim: silently dropping private tags would make those items public
        var tags = (settings.PrivateTags ?? string.Empty).Trim();
        if (tags.Length > MaxPrivateTagsLength)
        {
            return BadRequest(new { error = $"Private tags are limited to {MaxPrivateTagsLength} characters." });
        }

        var clean = new UserSettings
        {
            AlwaysPrivate = settings.AlwaysPrivate,
            PrivateLibraryIds = (settings.PrivateLibraryIds ?? []).Where(s => Guid.TryParse(s, out _)).Distinct().Take(100).ToArray(),
            PrivateTags = tags,
        };
        _store.SetSettings(id, clean);
        _tracker.Invalidate(id);
        return Ok(clean);
    }

    [HttpPost("Me/Unlink")]
    public async Task<ActionResult> UnlinkMe()
        => CallerId() is { } id ? await UnlinkCoreAsync(id).ConfigureAwait(false) : Unauthorized();

    // ---------------------------------------------------------------- admin

    [HttpGet("Accounts")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult GetAccounts()
        => Ok(_store.GetAccounts().Select(a => new
        {
            userId = a.UserId,
            userName = _userManager.GetUserById(a.UserId)?.Username ?? "(deleted user)",
            discordName = a.DiscordName,
            needsRelink = NeedsRelink(a),
        }));

    [HttpPost("Accounts/{userId}/Unlink")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult> Unlink([FromRoute] Guid userId) => UnlinkCoreAsync(userId);

    /// <summary>Shows what Discord would get for an item: the activity JSON, including which image is used.</summary>
    [HttpGet("Preview/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> Preview([FromRoute] Guid itemId)
    {
        var cfg = Plugin.Instance!.Configuration;
        var item = _libraryManager.GetItemById(itemId);
        var media = item is null ? null : MediaSnapshotFactory.Create(item);
        if (media is null)
        {
            return NotFound(new { error = "No playable movie, episode or track with that ID." });
        }

        // admin rules only; per-user rules can add to this
        media = media with { IsPrivate = _privacy.IsPrivate(item!, cfg) };
        var artwork = media.IsPrivate ? null : await _artwork.ResolveAsync(media, cfg).ConfigureAwait(false);
        var activity = PresenceMapper.Map(media, 0, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cfg, artwork, _host.FriendlyName);
        var source = PresenceMapper.PublicPosterUrl(media, cfg) is not null ? "public-url"
            : artwork is not null ? "lookup"
            : activity?.LargeImage is null ? "none" : "fallback";
        return Ok(new { isPrivate = media.IsPrivate, imageSource = source, activity = activity?.ToJson(cfg.ClientId) });
    }

    [HttpGet("ClientSecret")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult GetClientSecretState() => Ok(new { isSet = !string.IsNullOrEmpty(_store.ClientSecret) });

    /// <summary>Write-only: the secret is never sent back to a browser.</summary>
    [HttpPost("ClientSecret")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SetClientSecret([FromBody] SecretRequest request)
    {
        _store.ClientSecret = request.Value?.Trim() ?? string.Empty;
        return Ok(new { isSet = !string.IsNullOrEmpty(_store.ClientSecret) });
    }

    public sealed class SecretRequest
    {
        public string? Value { get; set; }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Rejected by Discord, or issued to a different Discord application than the configured one.</summary>
    private bool NeedsRelink(LinkedAccount account)
        => _sink.IsDead(account.UserId)
           || (!string.IsNullOrEmpty(account.ClientId) && !string.Equals(account.ClientId, DiscordClient.ClientId, StringComparison.Ordinal));

    private Guid? CallerId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out var id) && id != Guid.Empty ? id : null;
    }

    /// <summary>Stores a freshly approved link. Runs on its own timeout: the tokens exist now and must not be dropped.</summary>
    private async Task<ActionResult> CompleteLinkAsync(LinkedAccount account)
    {
        using var cts = new CancellationTokenSource(CommitTimeout);
        try
        {
            var me = await _discord.GetMeAsync(account.AccessToken, cts.Token).ConfigureAwait(false);
            account.DiscordName = me.Body.Str("global_name") ?? me.Body.Str("username") ?? "Discord user";
        }
        catch (OperationCanceledException)
        {
            account.DiscordName = "Discord user";
        }

        try
        {
            await _sink.RunExclusiveAsync(
                account.UserId,
                async () =>
                {
                    var replaced = _store.AddOrReplace(account);
                    if (replaced is not null)
                    {
                        await _sink.ClearAndForgetAsync(replaced, cts.Token).ConfigureAwait(false);
                    }

                    _sink.Reset(account.UserId);
                    return true;
                },
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogError(ex, "Could not store the Discord link for Jellyfin user {UserId}", account.UserId);
            await RevokeAsync(account).ConfigureAwait(false);
            return Ok(new { status = "failed", error = "The server couldn't save the link. Ask the admin to check the Jellyfin log, then try again." });
        }

        _tracker.Invalidate(account.UserId);
        _logger.LogInformation("Linked Jellyfin user {UserId} to Discord {Discord}", account.UserId, account.DiscordName);
        return Ok(new { status = "linked", discordName = account.DiscordName });
    }

    private async Task<ActionResult> UnlinkCoreAsync(Guid userId)
    {
        using var cts = new CancellationTokenSource(CommitTimeout);
        var account = await _sink.RunExclusiveAsync(
            userId,
            async () =>
            {
                var removed = _store.Remove(userId);
                if (removed is not null)
                {
                    await _sink.ClearAndForgetAsync(removed, cts.Token).ConfigureAwait(false);
                }

                return removed;
            },
            cts.Token).ConfigureAwait(false);
        if (account is null)
        {
            return NotFound(new { error = "Not linked." });
        }

        _tracker.Invalidate(userId);
        await RevokeAsync(account).ConfigureAwait(false);
        return Ok(new { unlinked = true });
    }

    /// <summary>Best effort: the link is already gone locally, and the user can revoke in Discord too.</summary>
    private async Task RevokeAsync(LinkedAccount account)
    {
        using var cts = new CancellationTokenSource(CommitTimeout);
        foreach (var (token, hint) in new[] { (account.RefreshToken, "refresh_token"), (account.AccessToken, "access_token") })
        {
            try
            {
                var res = await _discord.RevokeAsync(token, hint, cts.Token).ConfigureAwait(false);
                if (res.Status != 200)
                {
                    _logger.LogDebug("Revoking {Hint} returned {Status}", hint, res.Status);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Revoking Discord tokens for Jellyfin user {UserId} timed out", account.UserId);
                return;
            }
        }
    }

    private sealed class PendingLink
    {
        public required string DeviceCode { get; init; }

        public required string UserCode { get; init; }

        public required string VerificationUri { get; init; }

        public double IntervalSeconds { get; set; }

        public DateTimeOffset ExpiresAt { get; init; }

        public DateTimeOffset LastPoll { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public object ToDto() => new { status = "pending", userCode = UserCode, verificationUri = VerificationUri, expiresAt = ExpiresAt };
    }
}
