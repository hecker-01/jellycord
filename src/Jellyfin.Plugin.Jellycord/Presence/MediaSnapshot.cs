namespace Jellyfin.Plugin.Jellycord.Presence;

public enum MediaKind
{
    Movie,
    Episode,
    Music,
    Other,
}

/// <summary>The bits of a Jellyfin item that end up on Discord, decoupled from BaseItem so it can be tested.</summary>
public sealed record MediaSnapshot
{
    public required Guid ItemId { get; init; }

    public required MediaKind Kind { get; init; }

    public required string Title { get; init; }

    public string? SeriesName { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    public string? Artists { get; init; }

    public string? Album { get; init; }

    public int? Year { get; init; }

    public long? RunTimeTicks { get; init; }

    /// <summary>Item whose Primary image is the poster (series for episodes, album for tracks).</summary>
    public Guid ImageItemId { get; init; }
}
