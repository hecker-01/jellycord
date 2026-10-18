using Jellyfin.Plugin.Jellycord.Presence;

namespace Jellyfin.Plugin.Jellycord.Playback;

public enum PresenceAction
{
    None,
    Send,
    Clear,
}

/// <summary>Pure rules for when to talk to Discord. Kept separate from the tracker so they can be unit tested.</summary>
public static class PresenceDecision
{
    /// <summary>Discord allows ~5 presence updates per 20s; stay well below that.</summary>
    public const long MinSendIntervalMs = 4_000;

    /// <summary>Headless sessions expire after 20 minutes; refresh at 14.</summary>
    public const long HeartbeatMs = 14 * 60_000;

    /// <summary>A progress bar more than this far off means the user seeked.</summary>
    public const long SeekToleranceMs = 10_000;

    /// <param name="lastSent">What Discord currently shows (null = nothing).</param>
    /// <param name="lastSentAtMs">When it was sent.</param>
    /// <param name="desired">What it should show (null = nothing).</param>
    /// <param name="hasSessions">Whether the user still has any playback session.</param>
    /// <param name="stoppedAtMs">When the user's last session stopped, if they have none.</param>
    /// <param name="clearDelayMs">Grace period after stop before clearing.</param>
    public static PresenceAction Decide(
        PresenceActivity? lastSent,
        long lastSentAtMs,
        PresenceActivity? desired,
        bool hasSessions,
        long? stoppedAtMs,
        long clearDelayMs,
        long nowMs)
    {
        if (desired is null)
        {
            if (lastSent is null)
            {
                return PresenceAction.None;
            }

            // still playing but hidden (e.g. paused with "show when paused" off): clear right away.
            // nothing playing: wait out the grace period so autoplay into the next item doesn't flicker.
            if (hasSessions || stoppedAtMs is null || nowMs - stoppedAtMs.Value >= clearDelayMs)
            {
                return PresenceAction.Clear;
            }

            return PresenceAction.None;
        }

        var changed = lastSent is null
            || lastSent.ContentKey != desired.ContentKey
            || Drift(lastSent.StartMs, desired.StartMs) > SeekToleranceMs;
        var stale = nowMs - lastSentAtMs >= HeartbeatMs;

        if (!changed && !stale)
        {
            return PresenceAction.None;
        }

        return nowMs - lastSentAtMs >= MinSendIntervalMs ? PresenceAction.Send : PresenceAction.None;
    }

    private static long Drift(long? a, long? b)
        => a is null || b is null ? 0 : Math.Abs(a.Value - b.Value);
}
