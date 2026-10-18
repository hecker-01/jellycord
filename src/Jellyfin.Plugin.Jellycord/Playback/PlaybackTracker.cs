using Jellyfin.Plugin.Jellycord.Presence;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycord.Playback;

/// <summary>
/// Listens to Jellyfin playback events and keeps each user's Discord activity in sync.
/// Event handlers only record state; a 1s loop decides what (if anything) to send,
/// which gives debouncing, rate limiting and heartbeats in one place.
/// </summary>
public sealed class PlaybackTracker : IHostedService, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _sessionManager;
    private readonly IPresenceSink _sink;
    private readonly ILogger<PlaybackTracker> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, UserState> _users = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _lastReconcile;

    public PlaybackTracker(ISessionManager sessionManager, IPresenceSink sink, ILogger<PlaybackTracker> logger)
        : this(sessionManager, sink, logger, TimeProvider.System)
    {
    }

    internal PlaybackTracker(ISessionManager sessionManager, IPresenceSink sink, ILogger<PlaybackTracker> logger, TimeProvider time)
    {
        _sessionManager = sessionManager;
        _sink = sink;
        _logger = logger;
        _time = time;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackProgress;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.SessionEnded += OnSessionEnded;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Jellycord playback tracker started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackProgress;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded -= OnSessionEnded;

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // best effort: don't leave people "watching" while the server is down
        Guid[] shown;
        lock (_lock)
        {
            shown = [.. _users.Where(u => u.Value.LastSent is not null).Select(u => u.Key)];
        }

        using var clearCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var userId in shown)
        {
            try
            {
                await _sink.ClearAsync(userId, clearCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Forget what we sent for a user, e.g. after they (re)link, so current playback is re-sent.</summary>
    public void Invalidate(Guid userId)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(userId, out var u))
            {
                u.LastSent = null;
                u.LastSentAtMs = 0;
                u.Failures = 0;
                u.RetryAtMs = 0;
            }
        }
    }

    internal void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is null || e.Session is null || e.Session.UserId == Guid.Empty)
        {
            return;
        }

        var media = MediaSnapshotFactory.Create(e.Item);
        if (media is null)
        {
            return;
        }

        var report = new PlaybackReport(
            media,
            e.PlaybackPositionTicks ?? e.Session.PlayState?.PositionTicks ?? 0,
            e.IsPaused,
            NowMs);

        lock (_lock)
        {
            var user = GetUser(e.Session.UserId);
            user.Sessions[e.Session.Id] = report;
            user.StoppedAtMs = null;
        }
    }

    internal void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session is null || e.Session.UserId == Guid.Empty)
        {
            return;
        }

        RemoveSession(e.Session.UserId, e.Session.Id);
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        if (e.SessionInfo is { } s && s.UserId != Guid.Empty)
        {
            RemoveSession(s.UserId, s.Id);
        }
    }

    private void RemoveSession(Guid userId, string sessionId)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(userId, out var user) && user.Sessions.Remove(sessionId) && user.Sessions.Count == 0)
            {
                user.StoppedAtMs = NowMs;
            }
        }
    }

    private UserState GetUser(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var user))
        {
            user = new UserState();
            _users[userId] = user;
        }

        return user;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (_time.GetUtcNow() - _lastReconcile >= ReconcileEvery)
                {
                    Reconcile();
                    _lastReconcile = _time.GetUtcNow();
                }

                await ProcessAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jellycord presence cycle failed");
            }
        }
    }

    /// <summary>Drops sessions Jellyfin no longer considers playing (missed stop events, dead clients).</summary>
    private void Reconcile()
    {
        var live = _sessionManager.Sessions
            .Where(s => s.NowPlayingItem is not null)
            .ToDictionary(s => s.Id, s => s.NowPlayingItem!.Id);

        lock (_lock)
        {
            foreach (var (userId, user) in _users)
            {
                foreach (var (sessionId, report) in user.Sessions.ToArray())
                {
                    if (!live.TryGetValue(sessionId, out var itemId) || itemId != report.Media.ItemId)
                    {
                        RemoveSession(userId, sessionId);
                    }
                }
            }
        }
    }

    internal async Task ProcessAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return;
        }

        var now = NowMs;
        var work = new List<(Guid UserId, PresenceAction Action, PresenceActivity? Activity)>();
        lock (_lock)
        {
            foreach (var (userId, user) in _users)
            {
                if (now < user.RetryAtMs || !_sink.IsAvailableFor(userId))
                {
                    continue;
                }

                var best = user.Sessions.Values
                    .OrderBy(r => r.Paused)
                    .ThenByDescending(r => r.ReportedAtMs)
                    .FirstOrDefault();
                var desired = best is null
                    ? null
                    : PresenceMapper.Map(best.Media, best.PositionTicks, best.Paused, best.ReportedAtMs, cfg);

                var action = PresenceDecision.Decide(
                    user.LastSent,
                    user.LastSentAtMs,
                    desired,
                    user.Sessions.Count > 0,
                    user.StoppedAtMs,
                    Math.Max(0, cfg.ClearDelaySeconds) * 1000L,
                    now);
                if (action != PresenceAction.None)
                {
                    work.Add((userId, action, desired));
                }
            }

            // forget idle users we have nothing to say about
            foreach (var id in _users.Where(u => u.Value.Sessions.Count == 0 && u.Value.LastSent is null).Select(u => u.Key).ToArray())
            {
                _users.Remove(id);
            }
        }

        foreach (var (userId, action, activity) in work)
        {
            var ok = false;
            try
            {
                if (action == PresenceAction.Send)
                {
                    ok = await _sink.SetAsync(userId, activity!, ct).ConfigureAwait(false);
                    if (ok)
                    {
                        _logger.LogDebug("Presence for {User}: {Name} / {State}", userId, activity!.Name, activity.State);
                    }
                }
                else
                {
                    await _sink.ClearAsync(userId, ct).ConfigureAwait(false);
                    ok = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // one user's failure must not hold up everyone else
                _logger.LogError(ex, "Presence update for user {User} failed", userId);
            }

            Record(userId, ok ? (action == PresenceAction.Send ? activity : null) : null, ok, now);
        }
    }

    private void Record(Guid userId, PresenceActivity? shown, bool ok, long now)
    {
        lock (_lock)
        {
            var user = GetUser(userId);
            if (ok)
            {
                user.LastSent = shown;
                user.LastSentAtMs = now;
                user.Failures = 0;
                user.RetryAtMs = 0;
            }
            else
            {
                // back off 4s, 8s, 16s ... capped at 5 minutes
                user.Failures++;
                var delay = Math.Min(PresenceDecision.MinSendIntervalMs << Math.Min(user.Failures - 1, 10), 300_000);
                user.RetryAtMs = now + delay;
            }
        }
    }

    public void Dispose() => _cts?.Dispose();

    private sealed record PlaybackReport(MediaSnapshot Media, long PositionTicks, bool Paused, long ReportedAtMs);

    private sealed class UserState
    {
        public Dictionary<string, PlaybackReport> Sessions { get; } = [];

        public PresenceActivity? LastSent { get; set; }

        public long LastSentAtMs { get; set; }

        public long? StoppedAtMs { get; set; }

        public int Failures { get; set; }

        public long RetryAtMs { get; set; }
    }
}
