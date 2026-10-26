using System.Text;
using Jellyfin.Plugin.Jellycord.Api;
using Jellyfin.Plugin.Jellycord.Web;
using Microsoft.AspNetCore.Http;
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
        var html = IndexHtmlTransform.Transform("<html><body></body></html>");

        Assert.Matches(@"<script defer src=""\.\./jellycord/web\.js\?v=[0-9a-f]{12}""></script></body>", html);
        Assert.Equal(html, IndexHtmlTransform.Transform(html));
    }

    private const string Index = "<!DOCTYPE html><html><head></head><body><div id=\"reactRoot\"></div></body></html>";

    /// <summary>Runs the middleware in front of a stand-in for Jellyfin's static file handler.</summary>
    private static async Task<(HttpContext Context, string Body, string? SeenAcceptEncoding)> Serve(string path, string? ifNoneMatch = null)
    {
        string? seenEncoding = "untouched";
        var middleware = new IndexHtmlMiddleware(async ctx =>
        {
            seenEncoding = ctx.Request.Headers.AcceptEncoding.ToString();
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.LastModified = "Mon, 26 Oct 2026 10:00:00 GMT";
            var bytes = Encoding.UTF8.GetBytes(Index);
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes);
        });

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Headers.AcceptEncoding = "gzip, br";
        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        var body = new MemoryStream();
        context.Response.Body = body;
        await middleware.InvokeAsync(context);
        return (context, Encoding.UTF8.GetString(body.ToArray()), seenEncoding);
    }

    [Theory]
    [InlineData("/web/")]
    [InlineData("/web/index.html")]
    [InlineData("/jellyfin/web/index.html")] // base URL
    public async Task IndexHtml_IsServedWithTheScript(string path)
    {
        var (ctx, body, seenEncoding) = await Serve(path);

        Assert.Contains("jellycord/web.js?v=", body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), ctx.Response.ContentLength);
        Assert.Equal(string.Empty, seenEncoding); // asked Jellyfin for an uncompressed copy
        Assert.False(ctx.Response.Headers.ContainsKey("Last-Modified"));
        Assert.StartsWith("\"jc-", ctx.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task IndexHtml_RevalidatesWithItsOwnETag()
    {
        var (first, _, _) = await Serve("/web/");
        var (second, body, _) = await Serve("/web/", first.Response.Headers.ETag.ToString());

        Assert.Equal(304, second.Response.StatusCode);
        Assert.Empty(body);
    }

    [Fact]
    public async Task OtherFiles_PassThroughUntouched()
    {
        var (_, body, seenEncoding) = await Serve("/web/main.jellyfin.bundle.js");

        Assert.Equal(Index, body);
        Assert.Equal("gzip, br", seenEncoding);
    }
}
