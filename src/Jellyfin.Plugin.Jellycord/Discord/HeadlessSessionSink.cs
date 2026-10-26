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

    // One operation per user at a time: Discord rotates refresh tokens, so two concurrent refreshes
    // kill the link, and an update racing an unlink could re-create a session after it was deleted.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

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

    /// <summary>Runs <paramref name="work"/> while no presence update, clear or refresh for this user is in flight.</summary>
    public async Task<T> RunExclusiveAsync<T>(Guid userId, Func<Task<T>> work, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<bool> SetAsync(Guid userId, PresenceActivity activity, CancellationToken ct)
        => RunExclusiveAsync(userId, () => SetCoreAsync(userId, activity, ct), ct);

    public Task ClearAsync(Guid userId, CancellationToken ct)
        => RunExclusiveAsync(userId, () => ClearCoreAsync(userId, ct), ct);

    /// <summary>
    /// Clears using a specific (already unlinked or replaced) account, then forgets the user.
    /// Best effort: never throws. Call it inside <see cref="RunExclusiveAsync{T}"/>.
    /// </summary>
    public async Task ClearAndForgetAsync(LinkedAccount account, CancellationToken ct)
    {
        if (_sessionTokens.TryRemove(account.UserId, out var sessionToken))
        {
            try
            {
                await _discord.DeleteHeadlessSessionAsync(account.AccessToken, sessionToken, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Clear on unlink failed");
            }
        }

        _deadLinks.TryRemove(account.UserId, out _);
    }

    private async Task<bool> SetCoreAsync(Guid userId, PresenceActivity activity, CancellationToken ct)
    {
        // read inside the gate: an unlink or relink may have finished while we waited
        var account = _store.Find(userId);
        var appId = Plugin.Instance?.Configuration.ClientId.Trim();
        if (account is null || string.IsNullOrEmpty(appId))
        {
            return false;
        }

        try
        {
            var forcedRefresh = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await _discord.RefreshAsync(account, force: false, ct).ConfigureAwait(false);
                _sessionTokens.TryGetValue(userId, out var sessionToken);
                var res = await _discord.PostHeadlessSessionAsync(
                    account.AccessToken, activity.ToJson(appId), sessionToken, ct).ConfigureAwait(false);

                switch (res.Status)
                {
                    case 200 or 201:
                        var rotated = res.Body.Str("token");
                        if (rotated is not null)
                        {
                            _sessionTokens[userId] = rotated;
                        }

                        return true;
                    case 401 when !forcedRefresh:
                        // one forced refresh per call; a 401 that survives it isn't a token problem
                        forcedRefresh = true;
                        await _discord.RefreshAsync(account, force: true, ct).ConfigureAwait(false);
                        continue;
                    case 202 or 429:
                        // 202: Discord hasn't cached the app yet (retry_after in ms); 429: rate limited (seconds)
                        var retry = res.Body.Num("retry_after") ?? 5;
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
        catch (InvalidGrantException ex)
        {
            _deadLinks[userId] = true;
            _logger.LogWarning("Discord link for {User} needs to be made again: {Reason}", account.DiscordName, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Updating presence for {User} failed: {Message}", account.DiscordName, ex.Message);
        }

        return false;
    }

    private async Task<bool> ClearCoreAsync(Guid userId, CancellationToken ct)
    {
        if (!_sessionTokens.TryGetValue(userId, out var sessionToken))
        {
            return true;
        }

        var account = _store.Find(userId);
        if (account is null)
        {
            _sessionTokens.TryRemove(userId, out _);
            return true;
        }

        try
        {
            await _discord.RefreshAsync(account, force: false, ct).ConfigureAwait(false);
            var res = await _discord.DeleteHeadlessSessionAsync(account.AccessToken, sessionToken, ct).ConfigureAwait(false);
            _logger.LogDebug("Cleared presence for {User} ({Status})", account.DiscordName, res.Status);
            if (res.Status is >= 200 and < 300 or 404)
            {
                _sessionTokens.TryRemove(userId, out _);
            }
            else
            {
                // keep the token: the next update reuses the session, the next clear retries
                _logger.LogWarning("Clearing presence for {User} returned {Status}; it expires on its own otherwise", account.DiscordName, res.Status);
            }
        }
        catch (InvalidGrantException ex)
        {
            _sessionTokens.TryRemove(userId, out _);
            _deadLinks[userId] = true;
            _logger.LogWarning("Discord link for {User} needs to be made again: {Reason}", account.DiscordName, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Clearing presence for {User} failed; it will expire on its own", account.DiscordName);
        }

        return true;
    }
}
