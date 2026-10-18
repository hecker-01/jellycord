namespace Jellyfin.Plugin.Jellycord.Presence;

/// <summary>
/// Somewhere a user's activity can be delivered. The MVP ships the Discord
/// headless-sessions sink; a desktop IPC companion or a bot can plug in later.
/// </summary>
public interface IPresenceSink
{
    /// <summary>Whether this sink can currently show presence for the user (e.g. they linked Discord).</summary>
    bool IsAvailableFor(Guid userId);

    /// <summary>Sets or replaces the user's activity. Returns false if it should be retried later.</summary>
    Task<bool> SetAsync(Guid userId, PresenceActivity activity, CancellationToken ct);

    Task ClearAsync(Guid userId, CancellationToken ct);
}
