using System.Collections.Concurrent;
using System.Reflection;
using Jellyfin.Plugin.Jellycord.Configuration;
using Jellyfin.Plugin.Jellycord.Playback;
using Jellyfin.Plugin.Jellycord.Presence;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellycord.Tests;

public class PlaybackTrackerTests
{
    private static readonly PluginConfiguration Cfg = new() { ClearDelaySeconds = 0 };

    private readonly FakeClock _clock = new();
    private readonly FakeSink _sink = new();
    private readonly SessionsProxy _sessions;
    private readonly PlaybackTracker _tracker;
    private readonly Guid _user = Guid.NewGuid();

    public PlaybackTrackerTests()
    {
        _sessions = SessionsProxy.Create();
        _tracker = new PlaybackTracker((ISessionManager)(object)_sessions, _sink, null, NullLogger<PlaybackTracker>.Instance, _clock);
    }

    [Fact]
    public async Task TwoDevicesPlaying_KeepsShowingTheFirst()
    {
        Progress("tv", Movie("Alien"));
        _clock.Advance(1_000);
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);
        Assert.Equal("Alien", _sink.LastSet?.Name);

        // a second device starts and keeps reporting progress
        Progress("phone", Movie("Brazil"));
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(10_000);
            Progress("tv", Movie("Alien"));
            _clock.Advance(500);
            Progress("phone", Movie("Brazil")); // the newest report each time
            await _tracker.ProcessAsync(Cfg, CancellationToken.None);
        }

        Assert.Equal(1, _sink.Sets);
    }

    [Fact]
    public async Task StopForThePreviousItem_DoesNotDropTheNextOne()
    {
        var first = Movie("Alien");
        var next = Movie("Aliens");
        Progress("tv", first);
        Progress("tv", next); // autoplay: the next item starts before the stop for the first arrives
        _tracker.OnPlaybackStopped(null, new PlaybackStopEventArgs { Item = first, Session = Session("tv") });

        _clock.Advance(1_000);
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);

        Assert.Equal("Aliens", _sink.LastSet?.Name);
    }

    [Fact]
    public async Task SwitchingToATrailer_ClearsThePreviousItem()
    {
        Progress("tv", Movie("Alien"));
        _clock.Advance(1_000);
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);

        var trailer = Movie("Alien (trailer)");
        trailer.ExtraType = ExtraType.Trailer;
        Progress("tv", trailer);
        _clock.Advance(PresenceDecision.MinSendIntervalMs);
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);

        Assert.Equal(1, _sink.Clears);
    }

    [Fact]
    public async Task Reconcile_DropsSessionsJellyfinNoLongerPlays()
    {
        Progress("tv", Movie("Alien"));
        _clock.Advance(1_000);
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);

        _tracker.Reconcile(); // no live sessions
        _clock.Advance(PresenceDecision.MinSendIntervalMs);
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);

        Assert.Equal(1, _sink.Clears);
    }

    [Fact]
    public async Task Reconcile_LeavesReportsNewerThanItsSnapshot()
    {
        _clock.Advance(1_000);
        Progress("tv", Movie("Alien")); // reported at the same instant the snapshot is taken
        _tracker.Reconcile();
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);

        Assert.Equal("Alien", _sink.LastSet?.Name);
    }

    [Fact]
    public async Task FailedSends_BackOff()
    {
        _sink.SetResult = false;
        Progress("tv", Movie("Alien"));
        _clock.Advance(1_000);
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);
        _clock.Advance(1_000);
        await _tracker.ProcessAsync(Cfg, CancellationToken.None);

        Assert.Equal(1, _sink.Sets); // backing off, not retrying every tick
    }

    private void Progress(string sessionId, Movie item)
        => _tracker.OnPlaybackProgress(null, new PlaybackProgressEventArgs
        {
            // playing steadily since the test started, so reports don't look like seeks
            Item = item, Session = Session(sessionId), PlaybackPositionTicks = _clock.ElapsedMs * TimeSpan.TicksPerMillisecond,
        });

    private SessionInfo Session(string id)
        => new((ISessionManager)(object)_sessions, NullLogger.Instance) { Id = id, UserId = _user };

    private static readonly ConcurrentDictionary<string, Guid> Ids = new();

    private static Movie Movie(string name)
        => new() { Id = Ids.GetOrAdd(name, _ => Guid.NewGuid()), Name = name, RunTimeTicks = TimeSpan.FromHours(2).Ticks };

    private sealed class FakeClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = Start;

        public long ElapsedMs => (long)(_now - Start).TotalMilliseconds;

        public void Advance(long ms) => _now = _now.AddMilliseconds(ms);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeSink : IPresenceSink
    {
        private int _sets;
        private int _clears;

        public bool SetResult { get; set; } = true;

        public PresenceActivity? LastSet { get; private set; }

        public int Sets => _sets;

        public int Clears => _clears;

        public bool IsAvailableFor(Guid userId) => true;

        public Task<bool> SetAsync(Guid userId, PresenceActivity activity, CancellationToken ct)
        {
            Interlocked.Increment(ref _sets);
            LastSet = activity;
            return Task.FromResult(SetResult);
        }

        public Task ClearAsync(Guid userId, CancellationToken ct)
        {
            Interlocked.Increment(ref _clears);
            return Task.CompletedTask;
        }
    }

    /// <summary>An ISessionManager with no live sessions whose events can be subscribed to.</summary>
    public class SessionsProxy : DispatchProxy
    {
        public static SessionsProxy Create() => (SessionsProxy)(object)Create<ISessionManager, SessionsProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Sessions" => Array.Empty<SessionInfo>(),
                { } n when n.StartsWith("add_", StringComparison.Ordinal) || n.StartsWith("remove_", StringComparison.Ordinal) => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
    }
}
