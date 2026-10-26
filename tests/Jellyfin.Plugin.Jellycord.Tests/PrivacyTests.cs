using Jellyfin.Plugin.Jellycord.Configuration;
using Jellyfin.Plugin.Jellycord.Playback;
using Jellyfin.Plugin.Jellycord.Presence;
using Xunit;

namespace Jellyfin.Plugin.Jellycord.Tests;

public class PrivacyTests
{
    private static MediaSnapshot Private(MediaKind kind = MediaKind.Movie) => new()
    {
        ItemId = Guid.NewGuid(),
        Kind = kind,
        Title = "Secret Title",
        Year = 2024,
        RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
        IsPrivate = true,
    };

    [Fact]
    public void PrivateItem_ShowsOnlyServerName()
    {
        var a = PresenceMapper.Map(Private(), TimeSpan.FromMinutes(5).Ticks, false, 1_000_000, new PluginConfiguration(), "https://image.tmdb.org/t/p/w500/x.jpg", "heckrAni")!;

        Assert.Equal(PresenceActivity.Watching, a.Type);
        Assert.Equal("heckrAni", a.Name);
        Assert.Null(a.Details);
        Assert.Null(a.State);
        Assert.Null(a.StartMs);
        Assert.Null(a.EndMs);
        Assert.Null(a.SmallImage);
        Assert.Equal(PresenceMapper.LogoUrl, a.LargeImage);
        Assert.DoesNotContain("Secret", a.ToJson("1").ToJsonString());
    }

    [Fact]
    public void PrivateItem_IgnoresPublicPosterUrl()
    {
        var cfg = new PluginConfiguration { PublicUrl = "https://jf.example.com" };
        var a = PresenceMapper.Map(Private(), 0, false, 0, cfg, null, "heckrAni")!;
        Assert.Equal(PresenceMapper.LogoUrl, a.LargeImage);
    }

    [Fact]
    public void PrivateItem_PausedLooksTheSame()
    {
        var a = PresenceMapper.Map(Private(), 0, true, 0, new PluginConfiguration(), null, "heckrAni")!;
        Assert.Null(a.State);
    }

    [Fact]
    public void PrivateMusic_DoesNotRevealItsKind()
        => Assert.Equal(PresenceActivity.Watching, PresenceMapper.Map(Private(MediaKind.Music), 0, false, 0, new PluginConfiguration(), null, "x")!.Type);

    [Theory]
    [InlineData(MediaKind.Music)]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Episode)]
    public void PrivateItem_RespectsKindToggle(MediaKind kind)
    {
        var cfg = new PluginConfiguration { ShowMusic = false, ShowMovies = false, ShowEpisodes = false };
        Assert.Null(PresenceMapper.Map(Private(kind), 0, false, 0, cfg, null, "x"));
    }

    [Theory]
    [InlineData("XXX", "XXX")]
    [InlineData("de-18", "18")]
    [InlineData("NC-17", "NC-17")]
    [InlineData("TV-MA", "MA")]
    [InlineData(" R18+ ", "R18+")]
    public void Ratings_Normalize(string input, string expected) => Assert.Equal(expected, PrivacyFilter.Normalize(input));

    [Fact]
    public void Tags_AreNotRatingNormalized()
    {
        var tags = PrivacyFilter.SplitList("sci-fi, NSFW", normalizeRatings: false);
        Assert.Contains("sci-fi", tags);
        Assert.Contains("nsfw", tags); // case-insensitive
    }
}
