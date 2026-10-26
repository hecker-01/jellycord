using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycord.Web;

/// <summary>
/// If the File Transformation plugin is installed, injects web.js into jellyfin-web's index.html,
/// which adds a native "Discord status" entry to the user menu and Settings page.
/// File Transformation is loaded in its own context, so it is driven purely through reflection.
/// </summary>
public sealed class WebInjection : IHostedService
{
    private static readonly Guid TransformationId = Guid.Parse("4f1c2f0e-5c8a-4f53-9d1e-7a3b5c9e2d10");

    private readonly ILogger<WebInjection> _logger;

    public WebInjection(ILogger<WebInjection> logger)
    {
        _logger = logger;
    }

    private const int Attempts = 6;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // File Transformation finishes its own startup around the same time; retry briefly
        _ = Task.Run(
            async () =>
            {
                Exception? last = null;
                for (var attempt = 0; attempt < Attempts; attempt++)
                {
                    if (TryRegister(out last))
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }

                _logger.LogWarning(
                    last,
                    "Could not register with the File Transformation plugin; \"Discord status\" won't appear in jellyfin-web. Users can still link at /Jellycord/Link");
            },
            cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool TryRegister(out Exception? error)
    {
        error = null;
        var assembly = AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.FileTransformation");
        if (assembly is null)
        {
            _logger.LogInformation("File Transformation plugin not installed; Jellycord's user page is only at /Jellycord/Link");
            return true; // nothing to retry
        }

        try
        {
            var register = assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
                ?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            var payloadType = register?.GetParameters().FirstOrDefault()?.ParameterType;
            var parse = payloadType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            if (register is null || parse is null)
            {
                _logger.LogWarning("Unsupported File Transformation version; skipping web UI injection");
                return true;
            }

            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["id"] = TransformationId.ToString(),
                ["fileNamePattern"] = "index\\.html$",
                ["callbackAssembly"] = typeof(WebInjection).Assembly.FullName!,
                ["callbackClass"] = typeof(IndexHtmlTransform).FullName!,
                ["callbackMethod"] = nameof(IndexHtmlTransform.Transform),
            });
            register.Invoke(null, [parse.Invoke(null, [json])]);
            _logger.LogInformation("Jellycord added to the jellyfin-web user menu via File Transformation");
            return true;
        }
        catch (Exception ex)
        {
            // reflection wraps whatever File Transformation threw
            error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            _logger.LogDebug(error, "File Transformation not ready yet");
            return false;
        }
    }
}

/// <summary>Payload File Transformation hands to the callback.</summary>
public sealed class TransformPayload
{
    public string? Contents { get; set; }
}

/// <summary>Invoked by File Transformation (by name, via reflection) whenever index.html is served.</summary>
public static class IndexHtmlTransform
{
    private const string Marker = "Jellycord/web.js";

    /// <summary>Hash of the embedded script: a rebuilt web.js gets a new URL even without a version bump.</summary>
    private static readonly Lazy<string> ScriptHash = new(() =>
    {
        using var stream = typeof(IndexHtmlTransform).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Jellycord.Web.web.js");
        return stream is null ? "0" : Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
    });

    public static string Transform(TransformPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        if (Plugin.Instance?.Configuration.InjectWebUi == false || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var tag = $"<script defer src=\"../{Marker}?v={ScriptHash.Value}\"></script>";
        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + tag : html.Insert(at, tag);
    }
}
