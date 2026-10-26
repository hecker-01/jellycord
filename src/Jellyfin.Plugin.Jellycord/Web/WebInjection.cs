using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Jellycord.Web;

/// <summary>
/// Puts <see cref="IndexHtmlMiddleware"/> in front of Jellyfin's own pipeline, so web.js can be
/// added to jellyfin-web's index.html without any other plugin and without touching files on disk.
/// </summary>
public sealed class WebInjectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.UseMiddleware<IndexHtmlMiddleware>();
            next(app);
        };
}

/// <summary>Adds the web.js script tag to jellyfin-web's index.html as it is served.</summary>
public sealed class IndexHtmlMiddleware
{
    private readonly RequestDelegate _next;

    public IndexHtmlMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || !IsIndexHtml(context.Request.Path)
            || Plugin.Instance?.Configuration.InjectWebUi == false)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask Jellyfin for the plain, full file: a 304 for the untouched file would let browsers keep
        // a stale copy, and a compressed body can't be edited. index.html is a few KB.
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        if (context.Response.StatusCode != StatusCodes.Status200OK || context.Response.Headers.ContentEncoding.Count > 0)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var html = IndexHtmlTransform.Transform(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        var bytes = Encoding.UTF8.GetBytes(html);

        // our own validator for the edited page, so revalidation still gets cheap 304s
        var etag = $"\"jc-{Convert.ToHexStringLower(SHA256.HashData(bytes))[..16]}\"";
        context.Response.Headers.Remove(HeaderNames.LastModified);
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";
        if (string.Equals(ifNoneMatch, etag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.ContentLength = 0;
            return;
        }

        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>jellyfin-web's entry page, also under a base URL (/jellyfin/web/).</summary>
    internal static bool IsIndexHtml(PathString path)
    {
        var p = path.Value ?? string.Empty;
        return p.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The edit itself: one script tag before &lt;/body&gt;.</summary>
public static class IndexHtmlTransform
{
    private const string Marker = "jellycord/web.js";

    /// <summary>Hash of the embedded script: a rebuilt web.js gets a new URL even without a version bump.</summary>
    private static readonly Lazy<string> ScriptHash = new(() =>
    {
        using var stream = typeof(IndexHtmlTransform).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Jellycord.Web.web.js");
        return stream is null ? "0" : Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
    });

    public static string Transform(string html)
    {
        if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var tag = $"<script defer src=\"../{Marker}?v={ScriptHash.Value}\"></script>";
        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + tag : html.Insert(at, tag);
    }
}
