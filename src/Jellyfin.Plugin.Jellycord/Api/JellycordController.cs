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
    private static readonly ConcurrentDictionary<Guid, PendingLink> Pending = new();

    private readonly DiscordClient _discord;
    private readonly HeadlessSessionSink _sink;
    private readonly PlaybackTracker _tracker;
    private readonly CredentialStore _store;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ArtworkResolver _artwork;
    private readonly ILogger<JellycordController> _logger;

    public JellycordController(
        DiscordClient discord,
        HeadlessSessionSink sink,
        PlaybackTracker tracker,
        CredentialStore store,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ArtworkResolver artwork,
        ILogger<JellycordController> logger)
    {
        _discord = discord;
        _sink = sink;
        _tracker = tracker;
        _store = store;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _artwork = artwork;
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
            needsRelink = _sink.IsDead(id),
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
        if (res.Status != 200 || res.Body?["device_code"] is null)
        {
            _logger.LogWarning("Discord device authorize failed: {Status} {Body}", res.Status, res.Body?.ToJsonString());
            var hint = res.Status == 401 ? " Turn on 'Public Client' in the Discord app's OAuth2 settings, or set the client secret." : string.Empty;
            return StatusCode(502, new { error = $"Discord rejected the link request ({res.Status}).{hint}" });
        }

        var body = res.Body;
        var pending = new PendingLink
        {
            DeviceCode = body["device_code"]!.GetValue<string>(),
            UserCode = body["user_code"]!.GetValue<string>(),
            VerificationUri = body["verification_uri_complete"]?.GetValue<string>() ?? body["verification_uri"]!.GetValue<string>(),
            IntervalSeconds = body["interval"]?.GetValue<double>() ?? 5,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(body["expires_in"]?.GetValue<double>() ?? 300),
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
                return Ok(new { status = "linked", discordName = _store.Find(id)?.DiscordName });
            }

            // respect Discord's polling interval
            if ((DateTimeOffset.UtcNow - pending.LastPoll).TotalSeconds < pending.IntervalSeconds)
            {
                return Ok(new { status = "pending" });
            }

            pending.LastPoll = DateTimeOffset.UtcNow;
            var res = await _discord.DeviceTokenAsync(pending.DeviceCode, ct).ConfigureAwait(false);
            if (res.Status == 200 && res.Body?["access_token"] is not null)
            {
                Pending.TryRemove(id, out _);
                var account = new LinkedAccount { UserId = id };
                DiscordClient.ApplyTokens(account, res.Body);

                var me = await _discord.GetMeAsync(account.AccessToken, ct).ConfigureAwait(false);
                account.DiscordName = me.Body?["global_name"]?.GetValue<string>()
                    ?? me.Body?["username"]?.GetValue<string>()
                    ?? "Discord user";

                var replaced = _store.AddOrReplace(account);
                if (replaced is not null)
                {
                    await _sink.ClearAndForgetAsync(replaced, ct).ConfigureAwait(false);
                }

                _sink.Reset(id);
                _tracker.Invalidate(id);
                _logger.LogInformation("Linked Jellyfin user {UserId} to Discord {Discord}", id, account.DiscordName);
                return Ok(new { status = "linked", discordName = account.DiscordName });
            }

            switch (res.Error)
            {
                case "authorization_pending":
                    return Ok(new { status = "pending" });
                case "slow_down":
                    pending.IntervalSeconds += 5;
                    return Ok(new { status = "pending" });
                case null when res.Status == 0:
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

    [HttpPost("Me/Unlink")]
    public async Task<ActionResult> UnlinkMe(CancellationToken ct)
        => CallerId() is { } id ? await UnlinkCoreAsync(id, ct).ConfigureAwait(false) : Unauthorized();

    // ---------------------------------------------------------------- admin

    [HttpGet("Accounts")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult GetAccounts()
        => Ok(_store.GetAccounts().Select(a => new
        {
            userId = a.UserId,
            userName = _userManager.GetUserById(a.UserId)?.Username ?? "(deleted user)",
            discordName = a.DiscordName,
            needsRelink = _sink.IsDead(a.UserId),
        }));

    [HttpPost("Accounts/{userId}/Unlink")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult> Unlink([FromRoute] Guid userId, CancellationToken ct) => UnlinkCoreAsync(userId, ct);

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

        var artwork = await _artwork.ResolveAsync(media, cfg).ConfigureAwait(false);
        var activity = PresenceMapper.Map(media, 0, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cfg, artwork);
        var source = PresenceMapper.PublicPosterUrl(media, cfg) is not null ? "public-url"
            : artwork is not null ? "lookup"
            : activity?.LargeImage is null ? "none" : "fallback";
        return Ok(new { imageSource = source, activity = activity?.ToJson(cfg.ClientId) });
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

    private Guid? CallerId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out var id) && id != Guid.Empty ? id : null;
    }

    private async Task<ActionResult> UnlinkCoreAsync(Guid userId, CancellationToken ct)
    {
        var account = _store.Remove(userId);
        if (account is null)
        {
            return NotFound(new { error = "Not linked." });
        }

        await _sink.ClearAndForgetAsync(account, ct).ConfigureAwait(false);
        _tracker.Invalidate(userId);
        foreach (var (token, hint) in new[] { (account.RefreshToken, "refresh_token"), (account.AccessToken, "access_token") })
        {
            var res = await _discord.RevokeAsync(token, hint, ct).ConfigureAwait(false);
            if (res.Status != 200)
            {
                _logger.LogDebug("Revoking {Hint} returned {Status}", hint, res.Status);
            }
        }

        return Ok(new { unlinked = true });
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
