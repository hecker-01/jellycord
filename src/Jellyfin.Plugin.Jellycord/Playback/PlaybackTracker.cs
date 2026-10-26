using Jellyfin.Plugin.Jellycord.Discord;
using Jellyfin.Plugin.Jellycord.Presence;
using MediaBrowser.Controller.Entities;
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
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(10);

    /// <summary>How long a new item may wait for its poster lookup before going out with the fallback image.</summary>
    private const long ArtworkWaitMs = 4_000;

    /// <summary>A lookup that found nothing is tried again after this long, in case it was a transient failure.</summary>
    private const long ArtworkRetryMs = 5 * 60_000;

    /// <summary>Users are sent to in parallel, so one slow or rate-limited user doesn't hold up the rest.</summary>
    private const int MaxParallelSends = 4;

    private readonly ISessionManager _sessionManager;
    private readonly IPresenceSink _sink;
    private readonly ArtworkResolver? _artwork;
    private readonly PrivacyFilter? _privacy;
    private readonly CredentialStore? _store;
    private readonly ILogger<PlaybackTracker> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, UserState> _users = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _lastReconcile;

    public PlaybackTracker(
        ISessionManager sessionManager,
        IPresenceSink sink,
        ArtworkResolver artwork,
        PrivacyFilter privacy,
        CredentialStore store,
        ILogger<PlaybackTracker> logger)
        : this(sessionManager, sink, artwork, logger, TimeProvider.System)
    {
        _privacy = privacy;
        _store = store;
    }

    internal PlaybackTracker(ISessionManager sessionManager, IPresenceSink sink, ArtworkResolver? artwork, ILogger<PlaybackTracker> logger, TimeProvider time)
    {
        _sessionManager = sessionManager;
        _sink = sink;
        _artwork = artwork;
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
        Unsubscribe();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(ShutdownWait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }

        // best effort: don't leave people "watching" while the server is down
        Guid[] shown;
        lock (_lock)
        {
            shown = [.. _users.Where(u => u.Value.LastSent is not null).Select(u => u.Key)];
        }

        using var clearCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clearCts.CancelAfter(ShutdownWait);
        try
        {
            await Parallel.ForEachAsync(
                shown,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSends, CancellationToken = clearCts.Token },
                async (userId, ct) =>
                {
                    try
                    {
                        await _sink.ClearAsync(userId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Clearing presence for {User} on shutdown failed", userId);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
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

    /// <summary>Fails closed: if the rules can't be checked, the item is treated as private.</summary>
    private bool IsPrivate(BaseItem item, Guid userId)
    {
        if (Plugin.Instance?.Configuration is not { } cfg)
        {
            return true;
        }

        try
        {
            return _privacy!.IsPrivate(item, cfg, _store?.GetSettings(userId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Privacy check failed for item {ItemId}; treating it as private", item.Id);
            return true;
        }
    }

    // Event handlers run inside Jellyfin's playback reporting: they must never throw.
    internal void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            HandleProgress(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a playback event failed");
        }
    }

    internal void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            if (e.Session is not null && e.Session.UserId != Guid.Empty)
            {
                // autoplay can report Start(next) before Stop(previous): only remove the item that stopped
                RemoveSession(e.Session.UserId, e.Session.Id, e.Item?.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a playback stop failed");
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        try
        {
            if (e.SessionInfo is { } s && s.UserId != Guid.Empty)
            {
                RemoveSession(s.UserId, s.Id, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a session end failed");
        }
    }

    private void HandleProgress(PlaybackProgressEventArgs e)
    {
        if (e.Item is null || e.Session is null || e.Session.UserId == Guid.Empty)
        {
            return;
        }

        var media = MediaSnapshotFactory.Create(e.Item);
        if (media is null)
        {
            // trailers, extras, live TV...: not shown, and the item before it is no longer playing here
            RemoveSession(e.Session.UserId, e.Session.Id, null);
            return;
        }

        if (_privacy is not null && IsPrivate(e.Item, e.Session.UserId))
        {
            media = media with { IsPrivate = true };
        }

        var now = NowMs;
        lock (_lock)
        {
            var user = GetUser(e.Session.UserId);
            user.Sessions.TryGetValue(e.Session.Id, out var previous);
            var sameItem = previous?.Media.ItemId == media.ItemId;
            user.Sessions[e.Session.Id] = new PlaybackReport(
                media,
                e.PlaybackPositionTicks ?? e.Session.PlayState?.PositionTicks ?? 0,
                e.IsPaused,
                now,
                sameItem ? previous!.ItemSinceMs : now,
                sameItem ? previous!.Artwork : null,
                sameItem ? previous!.ArtworkAtMs : 0);
            user.StoppedAtMs = null;
        }
    }

    /// <param name="itemId">Only remove the session if it is still playing this item (null = whatever it plays).</param>
    private void RemoveSession(Guid userId, string sessionId, Guid? itemId)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(userId, out var user)
                || !user.Sessions.TryGetValue(sessionId, out var report)
                || (itemId is { } id && id != report.Media.ItemId))
            {
                return;
            }

            user.Sessions.Remove(sessionId);
            if (user.Sessions.Count == 0)
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

    private void Unsubscribe()
    {
        _sessionManager.PlaybackStart -= OnPlaybackProgress;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded -= OnSessionEnded;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (_time.GetUtcNow() - _lastReconcile >= ReconcileEvery)
            {
                // advance first: a failing reconcile must not run (and log) every second
                _lastReconcile = _time.GetUtcNow();
                try
                {
                    Reconcile();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Jellycord session reconcile failed");
                }
            }

            try
            {
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
    internal void Reconcile()
    {
        // The snapshot is taken outside our lock (Jellyfin may raise events while holding its own),
        // so reports newer than it are left alone rather than judged against stale data.
        var snapshotAt = NowMs;
        var live = _sessionManager.Sessions
            .Where(s => s.NowPlayingItem is not null)
            .ToDictionary(s => s.Id, s => s.NowPlayingItem!.Id);

        lock (_lock)
        {
            foreach (var (userId, user) in _users)
            {
                foreach (var (sessionId, report) in user.Sessions.ToArray())
                {
                    if (report.ReportedAtMs < snapshotAt
                        && (!live.TryGetValue(sessionId, out var itemId) || itemId != report.Media.ItemId))
                    {
                        RemoveSession(userId, sessionId, null);
                    }
                }
            }
        }
    }

    internal Task ProcessAsync(CancellationToken ct) => ProcessAsync(Plugin.Instance?.Configuration, ct);

    internal async Task ProcessAsync(Configuration.PluginConfiguration? cfg, CancellationToken ct)
    {
        if (cfg is null)
        {
            return;
        }

        var now = NowMs;
        var work = new List<(Guid UserId, PresenceAction Action, PresenceActivity? Activity)>();
        var lookups = new List<(Guid UserId, string SessionId, MediaSnapshot Media)>();
        lock (_lock)
        {
            foreach (var (userId, user) in _users)
            {
                if (!_sink.IsAvailableFor(userId))
                {
                    // unlinked or dead link: nothing we can clear, so forget it (and let it be pruned)
                    user.LastSent = null;
                    continue;
                }

                var bestPair = PickSession(user);
                var best = bestPair?.Value;
                string? artwork = null;
                // private items never get a poster lookup (or upload)
                if (best is not null && _artwork is not null && !best.Media.IsPrivate)
                {
                    if (best.Artwork is null)
                    {
                        // started outside the lock: the lookup's synchronous part hits the database
                        lookups.Add((userId, bestPair!.Value.Key, best.Media));
                        continue;
                    }

                    if (!best.Artwork.IsCompleted && now - best.ItemSinceMs < ArtworkWaitMs)
                    {
                        continue;
                    }

                    artwork = best.Artwork.IsCompletedSuccessfully ? best.Artwork.Result : null;
                    if (artwork is null && best.Artwork.IsCompleted && now - best.ArtworkAtMs >= ArtworkRetryMs)
                    {
                        lookups.Add((userId, bestPair!.Value.Key, best.Media));
                    }
                }

                var desired = best is null
                    ? null
                    : PresenceMapper.Map(best.Media, best.PositionTicks, best.Paused, best.ReportedAtMs, cfg, artwork);

                var action = PresenceDecision.Decide(
                    user.LastSent,
                    user.LastSentAtMs,
                    desired,
                    user.Sessions.Count > 0,
                    user.StoppedAtMs,
                    Math.Max(0, cfg.ClearDelaySeconds) * 1000L,
                    now);

                // a backoff after failed sends must not keep a stopped user "watching": clears go through
                if (action == PresenceAction.Send && now < user.RetryAtMs)
                {
                    continue;
                }

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

        StartLookups(lookups, cfg);

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSends, CancellationToken = ct },
            async (w, token) =>
            {
                var ok = false;
                try
                {
                    if (w.Action == PresenceAction.Send)
                    {
                        ok = await _sink.SetAsync(w.UserId, w.Activity!, token).ConfigureAwait(false);
                        if (ok)
                        {
                            _logger.LogDebug("Presence for {User}: {Name} / {State}", w.UserId, w.Activity!.Name, w.Activity.State);
                        }
                    }
                    else
                    {
                        await _sink.ClearAsync(w.UserId, token).ConfigureAwait(false);
                        ok = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    // one user's failure (or timeout) must not hold up everyone else
                    _logger.LogError(ex, "Presence update for user {User} failed", w.UserId);
                }

                Record(w.UserId, ok ? (w.Action == PresenceAction.Send ? w.Activity : null) : null, ok);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps showing the session already on Discord while it plays, so two devices playing at once
    /// don't swap places on every progress report; otherwise playing beats paused, then most recent.
    /// </summary>
    private static KeyValuePair<string, PlaybackReport>? PickSession(UserState user)
    {
        if (user.ShownSessionId is { } shown
            && user.Sessions.TryGetValue(shown, out var current)
            && !current.Paused)
        {
            return new(shown, current);
        }

        if (user.Sessions.Count == 0)
        {
            user.ShownSessionId = null;
            return null;
        }

        var best = user.Sessions
            .OrderBy(kv => kv.Value.Paused)
            .ThenByDescending(kv => kv.Value.ReportedAtMs)
            .First();
        user.ShownSessionId = best.Key;
        return best;
    }

    private void StartLookups(List<(Guid UserId, string SessionId, MediaSnapshot Media)> lookups, Configuration.PluginConfiguration cfg)
    {
        foreach (var (userId, sessionId, media) in lookups)
        {
            var task = _artwork!.ResolveAsync(media, cfg);
            var at = NowMs;
            lock (_lock)
            {
                // only if the session is still on that item
                if (_users.TryGetValue(userId, out var user)
                    && user.Sessions.TryGetValue(sessionId, out var report)
                    && report.Media.ItemId == media.ItemId)
                {
                    user.Sessions[sessionId] = report with { Artwork = task, ArtworkAtMs = at };
                }
            }
        }
    }

    private void Record(Guid userId, PresenceActivity? shown, bool ok)
    {
        lock (_lock)
        {
            var now = NowMs;
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
                // back off 6s, 12s, 24s ... capped at 5 minutes
                user.Failures++;
                var delay = Math.Min(PresenceDecision.MinSendIntervalMs << Math.Min(user.Failures - 1, 10), 300_000);
                user.RetryAtMs = now + delay;
            }
        }
    }

    public void Dispose()
    {
        Unsubscribe();
        if (Interlocked.Exchange(ref _cts, null) is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private sealed record PlaybackReport(
        MediaSnapshot Media,
        long PositionTicks,
        bool Paused,
        long ReportedAtMs,
        long ItemSinceMs,
        Task<string?>? Artwork,
        long ArtworkAtMs);

    private sealed class UserState
    {
        public Dictionary<string, PlaybackReport> Sessions { get; } = [];

        /// <summary>The session whose item Discord currently shows.</summary>
        public string? ShownSessionId { get; set; }

        public PresenceActivity? LastSent { get; set; }

        public long LastSentAtMs { get; set; }

        public long? StoppedAtMs { get; set; }

        public int Failures { get; set; }

        public long RetryAtMs { get; set; }
    }
}
