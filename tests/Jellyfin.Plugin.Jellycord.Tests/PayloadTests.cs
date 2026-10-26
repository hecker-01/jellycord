using Jellyfin.Plugin.Jellycord.Configuration;
using Jellyfin.Plugin.Jellycord.Playback;
using Jellyfin.Plugin.Jellycord.Presence;
using Xunit;

namespace Jellyfin.Plugin.Jellycord.Tests;

/// <summary>What ends up in the payload Discord validates, plus the URLs that go public.</summary>
public class PayloadTests
{
    private const long Now = 1_800_000_000_000;

    private static MediaSnapshot Movie(string title = "Alien", long? runtimeTicks = 2 * TimeSpan.TicksPerHour) => new()
    {
        ItemId = Guid.NewGuid(), Kind = MediaKind.Movie, Title = title, RunTimeTicks = runtimeTicks,
    };

    [Fact]
    public void EmptyTitle_StillHasAName()
        => Assert.Equal("Unknown title", PresenceMapper.Map(Movie(" "), 0, false, Now, new PluginConfiguration())!.Name);

    [Fact]
    public void Truncation_NeverSplitsASurrogatePair()
    {
        var fitted = PresenceMapper.Fit(new string('a', 126) + "😀😀");
        Assert.NotNull(fitted);
        Assert.True(fitted.Length <= 128);
        Assert.False(char.IsHighSurrogate(fitted[^2]));
    }

    [Fact]
    public void ControlCharacters_BecomeSpaces()
        => Assert.Equal("Line one  line two", PresenceMapper.Fit("Line one\r\nline two"));

    [Fact]
    public void LongPausedState_KeepsPaused()
    {
        var ep = new MediaSnapshot
        {
            ItemId = Guid.NewGuid(), Kind = MediaKind.Episode, SeriesName = "Show", Season = 1, Episode = 1,
            Title = new string('x', 200), RunTimeTicks = TimeSpan.TicksPerHour,
        };
        var a = PresenceMapper.Map(ep, 0, true, Now, new PluginConfiguration())!;
        Assert.EndsWith("· Paused", a.State);
        Assert.True(a.State!.Length <= 128);
    }

    [Fact]
    public void UnknownRuntime_ShowsElapsedTime()
    {
        var a = PresenceMapper.Map(Movie(runtimeTicks: null), 60 * TimeSpan.TicksPerSecond, false, Now, new PluginConfiguration())!;
        Assert.Equal(Now - 60_000, a.StartMs);
        Assert.Null(a.EndMs);

        var json = a.ToJson("1").ToJsonString();
        Assert.Contains("\"start\"", json);
        Assert.DoesNotContain("\"end\"", json);
    }

    [Fact]
    public void PositionPastRuntime_ShowsElapsedTimeNotAFullBar()
    {
        var a = PresenceMapper.Map(Movie(runtimeTicks: TimeSpan.TicksPerMinute), 2 * TimeSpan.TicksPerMinute, false, Now, new PluginConfiguration())!;
        Assert.NotNull(a.StartMs);
        Assert.Null(a.EndMs);
    }

    [Theory]
    [InlineData("https://user:pass@jf.example.com/jellyfin?x=1#y", "https://jf.example.com/jellyfin/Items/")]
    [InlineData("https://jf.example.com:8920/", "https://jf.example.com:8920/Items/")]
    public void PublicPosterUrl_KeepsOnlySchemeHostPortAndPath(string publicUrl, string expectedPrefix)
    {
        var url = PresenceMapper.PublicPosterUrl(Movie(), new PluginConfiguration { PublicUrl = publicUrl });
        Assert.NotNull(url);
        Assert.StartsWith(expectedPrefix, url);
        Assert.DoesNotContain("pass", url);
    }

    [Theory]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://100.101.102.103")]
    [InlineData("https://jellyfin.local")]
    [InlineData("https://jellyfin")]
    public void PublicPosterUrl_IgnoresPrivateServers(string publicUrl)
        => Assert.Null(PresenceMapper.PublicPosterUrl(Movie(), new PluginConfiguration { PublicUrl = publicUrl }));

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w500/x.jpg", true)]
    [InlineData("https://myserver.tail1234.ts.net/x.jpg", true)] // Tailscale Funnel is public
    [InlineData("http://image.tmdb.org/x.jpg", false)]
    [InlineData("https://10.0.0.5/x.jpg", false)]
    [InlineData("https://172.20.1.1/x.jpg", false)]
    [InlineData("https://169.254.1.1/x.jpg", false)]
    [InlineData("https://[fd00::1]/x.jpg", false)]
    [InlineData("https://[fe80::1]/x.jpg", false)]
    [InlineData("https://nas.lan/x.jpg", false)]
    [InlineData("https://u:p@cdn.example.com/x.jpg", false)]
    [InlineData("https://jf.example.com/Items/1/Images/Primary?api_key=abc", false)]
    public void IsPublicHttps(string url, bool expected) => Assert.Equal(expected, ArtworkResolver.IsPublicHttps(url));

    [Fact]
    public void Clear_IsRateLimitedToo()
    {
        var shown = new PresenceActivity { Type = PresenceActivity.Watching, Name = "Alien" };
        Assert.Equal(PresenceAction.None, PresenceDecision.Decide(shown, Now - 1_000, null, true, null, 0, Now));
        Assert.Equal(PresenceAction.Clear, PresenceDecision.Decide(shown, Now - PresenceDecision.MinSendIntervalMs, null, true, null, 0, Now));
    }
}
