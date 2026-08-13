using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PosterManager.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Full Cookie header for theposterdb.com. Required — TPDB sits behind Cloudflare
    /// and anonymous scraping gets challenged. Must contain cf_clearance.
    /// </summary>
    public string SessionCookie { get; set; } = string.Empty;

    /// <summary>
    /// User-Agent used for TPDB requests. Must match the browser the cf_clearance
    /// cookie was issued to, or Cloudflare rejects it.
    /// </summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Mediux API token, sent as "Authorization: Bearer &lt;token&gt;" to the
    /// Mediux GraphQL endpoint. Leave empty to disable the Mediux source.
    /// </summary>
    public string MediuxApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Preferred Mediux download quality: "original" or "optimized" (smaller jpg).
    /// </summary>
    public string MediuxDownloadQuality { get; set; } = "original";

    /// <summary>Delay between successive outbound scrape requests, in milliseconds.</summary>
    public int RequestDelayMs { get; set; } = 500;

    /// <summary>How long search/poster-list results stay cached, in minutes.</summary>
    public int CacheMinutes { get; set; } = 30;
}
