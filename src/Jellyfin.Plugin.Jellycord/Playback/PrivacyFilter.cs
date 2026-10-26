using System.Collections.Concurrent;
using Jellyfin.Plugin.Jellycord.Configuration;
using Jellyfin.Plugin.Jellycord.Discord;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycord.Playback;

/// <summary>
/// Decides whether an item is "private" (e.g. NSFW): such items only show
/// "Watching {server name}", with no title, artwork or progress.
/// </summary>
public sealed class PrivacyFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PrivacyFilter> _logger;
    private readonly ConcurrentDictionary<string, byte> _warnedIds = new(StringComparer.Ordinal);

    public PrivacyFilter(ILibraryManager libraryManager, ILogger<PrivacyFilter> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <param name="user">The viewer's own choices, which add to the admin's rules.</param>
    public bool IsPrivate(BaseItem item, PluginConfiguration cfg, UserSettings? user = null)
    {
        if (user?.AlwaysPrivate == true)
        {
            return true;
        }

        var libraryIds = cfg.PrivateLibraryIds.Concat(user?.PrivateLibraryIds ?? []).ToArray();
        if (libraryIds.Length > 0)
        {
            var hidden = new HashSet<Guid>(libraryIds.Select(ParseGuid).Where(g => g != Guid.Empty));
            if (hidden.Count > 0)
            {
                if (item.GetAncestorIds().Any(hidden.Contains)
                    || _libraryManager.GetCollectionFolders(item).Any(f => hidden.Contains(f.Id)))
                {
                    return true;
                }
            }
        }

        var tags = SplitList(cfg.PrivateTags + "," + user?.PrivateTags, normalizeRatings: false);
        if (tags.Count > 0)
        {
            // GetInheritedTags covers tags set on the series/season/album too
            var itemTags = item.GetInheritedTags().Concat(item.Tags ?? []);
            if (itemTags.Any(tags.Contains))
            {
                return true;
            }
        }

        var ratings = SplitList(cfg.PrivateRatings, normalizeRatings: true);
        if (ratings.Count > 0)
        {
            var rating = item.OfficialRatingForComparison ?? item.OfficialRating;
            if (!string.IsNullOrWhiteSpace(rating) && ratings.Contains(Normalize(rating)))
            {
                return true;
            }
        }

        return false;
    }

    internal static HashSet<string> SplitList(string? csv, bool normalizeRatings)
        => new(
            (csv ?? string.Empty)
                .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => normalizeRatings ? Normalize(s) : s),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Ratings come as "XXX", "de-18", "R18+"... compare case-insensitively, ignoring a country prefix.</summary>
    internal static string Normalize(string s)
    {
        s = s.Trim();
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        return dash is 2 or 3 && s.Length > dash + 1 && char.IsLetter(s[0]) && char.IsLetter(s[1]) && !s.StartsWith("NC-", StringComparison.OrdinalIgnoreCase)
            ? s[(dash + 1)..]
            : s;
    }

    private Guid ParseGuid(string s)
    {
        if (Guid.TryParse(s, out var g))
        {
            return g;
        }

        // a typo here means a library the admin meant to hide is shown; say so once per value
        if (_warnedIds.TryAdd(s, 0))
        {
            _logger.LogWarning("Ignoring private library id {Id}: not a valid id, so it hides nothing", s);
        }

        return Guid.Empty;
    }
}
