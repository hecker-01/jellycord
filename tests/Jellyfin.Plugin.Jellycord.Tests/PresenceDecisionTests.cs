using Jellyfin.Plugin.Jellycord.Playback;
using Jellyfin.Plugin.Jellycord.Presence;
using Xunit;

namespace Jellyfin.Plugin.Jellycord.Tests;

public class PresenceDecisionTests
{
    private const long T = 1_800_000_000_000;
    private const long Delay = 5_000;

    private static PresenceActivity Act(string name = "Movie", long? start = T, string? state = null) => new()
    {
        Type = PresenceActivity.Watching, Name = name, Details = name, State = state, StartMs = start, EndMs = start + 1000,
    };

    private static PresenceAction Decide(PresenceActivity? last, long lastAt, PresenceActivity? desired, bool sessions = true, long? stoppedAt = null, long now = T)
        => PresenceDecision.Decide(last, lastAt, desired, sessions, stoppedAt, Delay, now);

    [Fact]
    public void FirstActivity_Sends() => Assert.Equal(PresenceAction.Send, Decide(null, 0, Act()));

    [Fact]
    public void SameActivity_DoesNothing() => Assert.Equal(PresenceAction.None, Decide(Act(), T - 60_000, Act()));

    [Fact]
    public void SmallDrift_IsNotASeek() => Assert.Equal(PresenceAction.None, Decide(Act(start: T), T - 60_000, Act(start: T + 3_000)));

    [Fact]
    public void Seek_Resends() => Assert.Equal(PresenceAction.Send, Decide(Act(start: T), T - 60_000, Act(start: T - 120_000)));

    [Fact]
    public void ItemChange_Resends() => Assert.Equal(PresenceAction.Send, Decide(Act("A"), T - 60_000, Act("B")));

    [Fact]
    public void Pause_Resends() => Assert.Equal(PresenceAction.Send, Decide(Act(), T - 60_000, Act(start: null, state: "Paused")));

    [Fact]
    public void RateLimited_Waits() => Assert.Equal(PresenceAction.None, Decide(Act("A"), T - 1_000, Act("B")));

    [Fact]
    public void Heartbeat_ResendsUnchanged()
        => Assert.Equal(PresenceAction.Send, Decide(Act(), T - PresenceDecision.HeartbeatMs, Act()));

    [Fact]
    public void Stop_WaitsForGracePeriod()
        => Assert.Equal(PresenceAction.None, Decide(Act(), T - 60_000, null, sessions: false, stoppedAt: T - 2_000));

    [Fact]
    public void Stop_ClearsAfterGracePeriod()
        => Assert.Equal(PresenceAction.Clear, Decide(Act(), T - 60_000, null, sessions: false, stoppedAt: T - Delay));

    [Fact]
    public void HiddenWhilePlaying_ClearsImmediately()
        => Assert.Equal(PresenceAction.Clear, Decide(Act(), T - 60_000, null, sessions: true));

    [Fact]
    public void NothingShown_NothingToClear() => Assert.Equal(PresenceAction.None, Decide(null, 0, null, sessions: false, stoppedAt: T - 60_000));
}
