using Jellyfin.Plugin.Jellycord.Api;
using Jellyfin.Plugin.Jellycord.Web;
using Xunit;

namespace Jellyfin.Plugin.Jellycord.Tests;

public class WebTests
{
    [Theory]
    [InlineData("https://discord.com/activate?user_code=ABCD", true)]
    [InlineData("https://ptb.discord.com/activate", true)]
    [InlineData("http://discord.com/activate", false)]
    [InlineData("https://discord.com.evil.example/activate", false)]
    [InlineData("https://evildiscord.com/activate", false)]
    [InlineData("https://user@discord.com/activate", false)]
    [InlineData("javascript:alert(1)", false)]
    public void VerificationUrl_MustBeDiscord(string url, bool expected)
        => Assert.Equal(expected, JellycordController.IsDiscordUrl(url));

    [Fact]
    public void IndexHtml_GetsTheScriptOnceWithAContentHash()
    {
        var html = IndexHtmlTransform.Transform(new TransformPayload { Contents = "<html><body></body></html>" });

        Assert.Matches(@"<script defer src=""\.\./Jellycord/web\.js\?v=[0-9a-f]{12}""></script></body>", html);
        Assert.Equal(html, IndexHtmlTransform.Transform(new TransformPayload { Contents = html }));
    }
}
