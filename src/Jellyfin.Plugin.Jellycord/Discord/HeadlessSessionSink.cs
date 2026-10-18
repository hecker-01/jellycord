using System.Collections.Concurrent;
using Jellyfin.Plugin.Jellycord.Presence;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycord.Discord;

/// <summary>Delivers presence server-side via Discord's headless-sessions API, one session per linked user.</summary>
public sealed class HeadlessSessionSink : IPresenceSink
{
    private readonly DiscordClient _discord;
    private readonly CredentialStore _store;
    private readonly ILogger<HeadlessSessionSink> _logger;

    // the session token rotates on every successful post
    private readonly ConcurrentDictionary<Guid, string> _sessionTokens = new();
    private readonly ConcurrentDictionary<Guid, bool> _deadLinks = new();

    public HeadlessSessionSink(DiscordClient discord, CredentialStore store, ILogger<HeadlessSessionSink> logger)
    {
        _discord = discord;
        _store = store;
        _logger = logger;
    }

    public bool IsAvailableFor(Guid userId)
        => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ClientId)
           && !_deadLinks.ContainsKey(userId)
           && _store.Find(userId) is not null;

    /// <summary>Called after a (re)link so a previously dead link is retried.</summary>
    public void Reset(Guid userId)
    {
        _deadLinks.TryRemove(userId, out _);
        _sessionTokens.TryRemove(userId, out _);
    }

    /// <summary>True when the user's link was rejected by Discord and they must link again.</summary>
    public bool IsDead(Guid userId) => _deadLinks.ContainsKey(userId);

    public async Task<bool> SetAsync(Guid userId, PresenceActivity activity, CancellationToken ct)
    {
        var account = _store.Find(userId);
        var appId = Plugin.Instance?.Configuration.ClientId.Trim();
        if (account is null || string.IsNullOrEmpty(appId))
        {
            return false;
        }

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await _discord.RefreshAsync(account, force: false, ct).ConfigureAwait(false);
                _sessionTokens.TryGetValue(userId, out var sessionToken);
                var res = await _discord.PostHeadlessSessionAsync(
                    account.AccessToken, activity.ToJson(appId), sessionToken, ct).ConfigureAwait(false);

                switch (res.Status)
                {
                    case 200 or 201:
                        var rotated = res.Body?["token"]?.GetValue<string>();
                        if (rotated is not null)
                        {
                            _sessionTokens[userId] = rotated;
                        }

                        return true;
                    case 401:
                        await _discord.RefreshAsync(account, force: true, ct).ConfigureAwait(false);
                        continue;
                    case 202 or 429:
                        // 202: Discord hasn't cached the app yet (retry_after in ms); 429: rate limited (seconds)
                        var retry = res.Body?["retry_after"]?.GetValue<double>() ?? 5;
                        var seconds = res.Status == 202 ? retry / 1000 : retry;
                        if (seconds > 10)
                        {
                            _logger.LogInformation("Discord asked {User} to wait {Seconds:0}s", account.DiscordName, seconds);
                            return false;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(seconds, 0.5)), ct).ConfigureAwait(false);
                        continue;
                    default:
                        if (sessionToken is not null)
                        {
                            // session expired or token out of sync: start a fresh one
                            _sessionTokens.TryRemove(userId, out _);
                            continue;
                        }

                        _logger.LogWarning(
                            "Headless session post for {User} failed: {Status} {Body}",
                            account.DiscordName,
                            res.Status,
                            res.Body?.ToJsonString());
                        return false;
                }
            }
        }
        catch (InvalidGrantException)
        {
            _deadLinks[userId] = true;
            _logger.LogWarning("Discord link for {User} is no longer valid; they need to link again", account.DiscordName);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Updating presence for {User} failed: {Message}", account.DiscordName, ex.Message);
        }

        return false;
    }

    public async Task ClearAsync(Guid userId, CancellationToken ct)
    {
        if (!_sessionTokens.TryRemove(userId, out var sessionToken))
        {
            return;
        }

        var account = _store.Find(userId);
        if (account is null)
        {
            return;
        }

        try
        {
            await _discord.RefreshAsync(account, force: false, ct).ConfigureAwait(false);
            var res = await _discord.DeleteHeadlessSessionAsync(account.AccessToken, sessionToken, ct).ConfigureAwait(false);
            _logger.LogDebug("Cleared presence for {User} ({Status})", account.DiscordName, res.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Clearing presence for {User} failed; it will expire on its own", account.DiscordName);
        }
    }

    /// <summary>Clears using a specific (already unlinked) account, then forgets the user.</summary>
    public async Task ClearAndForgetAsync(LinkedAccount account, CancellationToken ct)
    {
        if (_sessionTokens.TryRemove(account.UserId, out var sessionToken))
        {
            try
            {
                await _discord.DeleteHeadlessSessionAsync(account.AccessToken, sessionToken, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Clear on unlink failed");
            }
        }

        _deadLinks.TryRemove(account.UserId, out _);
    }
}
