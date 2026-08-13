using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterManager.Web;

/// <summary>
/// Injects the client script into jellyfin-web's index.html. Jellyfin has no supported
/// hook for adding UI to the image editor, so the script tag is patched in at startup.
/// Re-applied on every start because a server upgrade rewrites index.html.
/// </summary>
public class WebInjectionService : IHostedService
{
    private const string Marker = "PosterManager/ClientScript";

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<WebInjectionService> _logger;

    public WebInjectionService(IApplicationPaths applicationPaths, ILogger<WebInjectionService> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Inject();
        }
        catch (Exception ex)
        {
            // Never block server startup over the UI hook.
            _logger.LogError(ex, "Failed to inject the Poster Manager client script");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Inject()
    {
        var webPath = _applicationPaths.WebPath;
        if (string.IsNullOrWhiteSpace(webPath))
        {
            _logger.LogWarning("WebPath is not set; skipping client script injection");
            return;
        }

        var indexPath = Path.Combine(webPath, "index.html");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("index.html not found at {Path}; skipping injection", indexPath);
            return;
        }

        var html = File.ReadAllText(indexPath);

        // Cache-bust with the plugin version so clients pick up a new script after upgrades.
        var version = Plugin.Instance?.Version?.ToString() ?? "1.0.0.0";
        var tag = $"<script defer src=\"../PosterManager/ClientScript?v={version}\"></script>";

        // Idempotent: drop any tag we injected previously (any ?v=), then add the current one.
        var existing = new Regex(
            @"\s*<script[^>]*src=""[^""]*" + Regex.Escape(Marker) + @"[^""]*""[^>]*>\s*</script>",
            RegexOptions.IgnoreCase);
        html = existing.Replace(html, string.Empty);

        // Already current — nothing to write, so don't touch the file.
        if (html.Contains(tag, StringComparison.Ordinal))
        {
            return;
        }

        var closingBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closingBody < 0)
        {
            _logger.LogWarning("No </body> in index.html; skipping injection");
            return;
        }

        html = html.Insert(closingBody, tag);

        try
        {
            File.WriteAllText(indexPath, html, Encoding.UTF8);
            _logger.LogInformation("Injected Poster Manager client script into {Path}", indexPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(
                ex,
                "No write access to {Path}. Grant Jellyfin write permission to the jellyfin-web "
                + "folder, or the poster button will not appear",
                indexPath);
        }
    }
}
