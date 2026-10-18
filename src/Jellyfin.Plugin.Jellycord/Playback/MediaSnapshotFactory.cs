using Jellyfin.Plugin.Jellycord.Presence;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Jellycord.Playback;

internal static class MediaSnapshotFactory
{
    /// <summary>Returns null for things that shouldn't become a status (theme songs, trailers, extras).</summary>
    public static MediaSnapshot? Create(BaseItem item)
    {
        if (item.ExtraType is not null || item.IsThemeMedia)
        {
            return null;
        }

        return item switch
        {
            Episode ep => new MediaSnapshot
            {
                ItemId = ep.Id,
                Kind = MediaKind.Episode,
                Title = ep.Name ?? string.Empty,
                SeriesName = ep.SeriesName,
                Season = ep.ParentIndexNumber,
                Episode = ep.IndexNumber,
                Year = ep.ProductionYear,
                RunTimeTicks = ep.RunTimeTicks,
                ImageItemId = ep.SeriesId == Guid.Empty ? ep.Id : ep.SeriesId,
            },
            Movie movie => new MediaSnapshot
            {
                ItemId = movie.Id,
                Kind = MediaKind.Movie,
                Title = movie.Name ?? string.Empty,
                Year = movie.ProductionYear,
                RunTimeTicks = movie.RunTimeTicks,
                ImageItemId = movie.Id,
            },
            Audio track => new MediaSnapshot
            {
                ItemId = track.Id,
                Kind = MediaKind.Music,
                Title = track.Name ?? string.Empty,
                Artists = JoinOrNull(track.Artists) ?? JoinOrNull(track.AlbumArtists),
                Album = string.IsNullOrWhiteSpace(track.Album) ? null : track.Album,
                Year = track.ProductionYear,
                RunTimeTicks = track.RunTimeTicks,
                ImageItemId = track.AlbumEntity?.Id ?? track.Id,
            },
            Video video => new MediaSnapshot
            {
                // home videos, music videos, etc. — shown like a movie
                ItemId = video.Id,
                Kind = MediaKind.Movie,
                Title = video.Name ?? string.Empty,
                Year = video.ProductionYear,
                RunTimeTicks = video.RunTimeTicks,
                ImageItemId = video.Id,
            },
            _ => null,
        };
    }

    private static string? JoinOrNull(IReadOnlyList<string>? values)
        => values is { Count: > 0 } ? string.Join(", ", values) : null;
}
