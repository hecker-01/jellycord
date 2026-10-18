using Jellyfin.Plugin.Jellycord.Configuration;
using Jellyfin.Plugin.Jellycord.Presence;
using Xunit;

namespace Jellyfin.Plugin.Jellycord.Tests;

public class PresenceMapperTests
{
    private const long Now = 1_800_000_000_000;
    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    private static MediaSnapshot Episode() => new()
    {
        ItemId = Guid.NewGuid(),
        Kind = MediaKind.Episode,
        Title = "Ozymandias",
        SeriesName = "Breaking Bad",
        Season = 5,
        Episode = 14,
        RunTimeTicks = 47 * Minute,
        ImageItemId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
    };

    [Fact]
    public void Episode_ShowsSeriesAndEpisodeCode()
    {
        var a = PresenceMapper.Map(Episode(), 10 * Minute, false, Now, new PluginConfiguration())!;

        Assert.Equal(PresenceActivity.Watching, a.Type);
        Assert.Equal("Breaking Bad", a.Name);
        Assert.Equal("S5E14 · Ozymandias", a.State);
        Assert.Equal(Now - 600_000, a.StartMs);
        Assert.Equal(a.StartMs + (47 * 60_000), a.EndMs);
    }

    [Fact]
    public void Paused_DropsTimestampsAndSaysPaused()
    {
        var a = PresenceMapper.Map(Episode(), 10 * Minute, true, Now, new PluginConfiguration())!;

        Assert.Null(a.StartMs);
        Assert.EndsWith("Paused", a.State);
    }

    [Fact]
    public void Paused_HiddenWhenConfigured()
    {
        var cfg = new PluginConfiguration { ShowWhenPaused = false };
        Assert.Null(PresenceMapper.Map(Episode(), 0, true, Now, cfg));
    }

    [Fact]
    public void Music_IsListening()
    {
        var song = new MediaSnapshot
        {
            ItemId = Guid.NewGuid(), Kind = MediaKind.Music, Title = "Paranoid Android",
            Artists = "Radiohead", Album = "OK Computer", RunTimeTicks = 6 * Minute,
        };
        var a = PresenceMapper.Map(song, 0, false, Now, new PluginConfiguration())!;

        Assert.Equal(PresenceActivity.Listening, a.Type);
        Assert.Equal("by Radiohead · OK Computer", a.State);
        Assert.Equal("OK Computer", a.LargeText);
    }

    [Fact]
    public void DisabledKind_ReturnsNull()
    {
        var cfg = new PluginConfiguration { ShowEpisodes = false };
        Assert.Null(PresenceMapper.Map(Episode(), 0, false, Now, cfg));
    }

    [Fact]
    public void PublicUrl_BuildsTokenFreePosterUrl()
    {
        var cfg = new PluginConfiguration { PublicUrl = "https://jf.example.com/" };
        var a = PresenceMapper.Map(Episode(), 0, false, Now, cfg)!;

        Assert.Equal(
            "https://jf.example.com/Items/11111111222233334444555555555555/Images/Primary?fillHeight=512&quality=90",
            a.LargeImage);
        Assert.DoesNotContain("api_key", a.LargeImage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://jf.example.com")]
    [InlineData("https://localhost:8920")]
    [InlineData("not a url")]
    public void UnusablePublicUrl_FallsBack(string url)
    {
        var cfg = new PluginConfiguration { PublicUrl = url, FallbackImage = "jellyfin" };
        Assert.Equal("jellyfin", PresenceMapper.Map(Episode(), 0, false, Now, cfg)!.LargeImage);
    }

    [Fact]
    public void Text_IsPaddedAndTruncated()
    {
        Assert.Equal(2, PresenceMapper.Fit("X")!.Length);
        Assert.Equal(128, PresenceMapper.Fit(new string('a', 300))!.Length);
        Assert.Null(PresenceMapper.Fit("   "));
    }

    [Fact]
    public void Json_UsesStringTimestampsAndOmitsNulls()
    {
        var a = PresenceMapper.Map(Episode(), 0, false, Now, new PluginConfiguration())!;
        var json = a.ToJson("123").ToJsonString();

        Assert.Contains("\"start\":\"1800000000000\"", json);
        Assert.DoesNotContain("assets", json);
        Assert.Contains("\"application_id\":\"123\"", json);
    }
}
