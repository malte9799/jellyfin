using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterManager.Providers;

/// <summary>
/// Scrapes theposterdb.com. TPDB has no public API, so this parses the site markup.
/// Selectors here are verified against real pages — see comments per method.
/// </summary>
public sealed class ThePosterDbSource : IPosterSource
{
    private const string BaseUrl = "https://theposterdb.com";

    private static readonly Regex PosterPageIdRegex = new(@"/posters/(\d+)", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ThePosterDbSource> _logger;

    public ThePosterDbSource(IHttpClientFactory httpClientFactory, ILogger<ThePosterDbSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string SourceId => PosterSourceIds.ThePosterDb;

    public string DisplayName => "ThePosterDB";

    // Cloudflare blocks anonymous scraping, so a cf_clearance cookie is mandatory.
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.SessionCookie);

    private HttpClient CreateClient()
    {
        var config = Plugin.Instance!.Configuration;
        var client = _httpClientFactory.CreateClient(nameof(ThePosterDbSource));
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        if (!string.IsNullOrWhiteSpace(config.SessionCookie))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", config.SessionCookie);
        }

        return client;
    }

    /// <summary>
    /// Search results live at /search?term=&amp;section=movies|shows|collections.
    /// Each hit is an anchor to /posters/{id} with the title in a child &lt;strong&gt;.
    /// Free search cards carry no year, so matching is title-only.
    /// </summary>
    public async Task<IReadOnlyList<PosterSet>> FindSetsAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        var section = query.Kind switch
        {
            ItemKind.Movie => "movies",
            ItemKind.Series => "shows",
            ItemKind.Collection => "collections",
            _ => "movies"
        };

        var url = $"{BaseUrl}/search?term={HttpUtility.UrlEncode(query.Title)}&section={section}";

        using var client = CreateClient();
        var html = await GetStringAsync(client, url, cancellationToken).ConfigureAwait(false);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<PosterSet>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var nodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '/posters/')]");
        if (nodes is null)
        {
            _logger.LogInformation("TPDB search for {Term} returned no result cards", query.Title);
            return results;
        }

        foreach (var node in nodes)
        {
            var href = node.GetAttributeValue("href", string.Empty);
            var match = PosterPageIdRegex.Match(href);
            if (!match.Success || !seen.Add(match.Groups[1].Value))
            {
                continue;
            }

            var strong = node.SelectSingleNode(".//strong");
            var title = HtmlEntity.DeEntitize(strong?.InnerText ?? node.InnerText)?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            results.Add(new PosterSet
            {
                Id = match.Groups[1].Value,
                SourceId = SourceId,
                Title = title,
                Subtitle = null
            });
        }

        return results;
    }

    /// <summary>
    /// A poster page is /posters/{id}. Each card is div.overlay[data-poster-id];
    /// the canonical full-res download is /api/assets/{posterId}. Follows a[rel=next].
    /// </summary>
    public async Task<IReadOnlyList<PosterImage>> GetImagesAsync(string setId, ItemKind kind, CancellationToken cancellationToken)
    {
        using var client = CreateClient();

        var images = new List<PosterImage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var url = $"{BaseUrl}/posters/{setId}";
        var delay = Math.Max(0, Plugin.Instance!.Configuration.RequestDelayMs);

        // Bounded so a pagination bug can never spin forever.
        for (var page = 0; page < 20 && url is not null; page++)
        {
            if (page > 0 && delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var html = await GetStringAsync(client, url, cancellationToken).ConfigureAwait(false);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var cards = doc.DocumentNode.SelectNodes("//div[@data-poster-id]");
            if (cards is not null)
            {
                foreach (var card in cards)
                {
                    var posterId = card.GetAttributeValue("data-poster-id", string.Empty);
                    if (string.IsNullOrWhiteSpace(posterId) || !seen.Add(posterId))
                    {
                        continue;
                    }

                    // data-poster-type is the *media* type ("movie"/"show"), not the image
                    // kind. The image kind is only inferable from the card title, where
                    // TPDB labels non-primary art explicitly.
                    var title = ExtractTitle(card);

                    images.Add(new PosterImage
                    {
                        Id = posterId,
                        SourceId = SourceId,
                        FullUrl = $"{BaseUrl}/api/assets/{posterId}",
                        // No usable fallback: the <img> src is a "missing poster" placeholder,
                        // so fall back to the full asset rather than that graphic.
                        ThumbnailUrl = ExtractThumbnail(card) ?? $"{BaseUrl}/api/assets/{posterId}",
                        ImageType = InferImageType(title),
                        Title = title,
                        Author = ExtractAuthor(card)
                    });
                }
            }

            var next = doc.DocumentNode.SelectSingleNode("//a[@rel='next']");
            var nextHref = next?.GetAttributeValue("href", null);
            url = string.IsNullOrWhiteSpace(nextHref) ? null : Absolute(nextHref);
        }

        return images;
    }

    public async Task<(Stream Stream, string ContentType)> DownloadAsync(string imageUrl, CancellationToken cancellationToken)
    {
        // Only ever fetch from TPDB itself — imageUrl reaches us from the client.
        if (!IsTrustedUrl(imageUrl))
        {
            throw new InvalidOperationException($"Refusing to download non-ThePosterDB URL: {imageUrl}");
        }

        using var client = CreateClient();
        var response = await client.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return (new MemoryStream(bytes), contentType);
    }

    internal static bool IsTrustedUrl(string imageUrl) =>
        Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("theposterdb.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".theposterdb.com", StringComparison.OrdinalIgnoreCase));

    private async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        // Cloudflare answers a stale/absent clearance cookie with 403/503 rather than a redirect.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
        {
            throw new InvalidOperationException(
                "ThePosterDB refused the request (Cloudflare). Refresh the SessionCookie "
                + "(cf_clearance) and make sure UserAgent matches the browser it came from.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Thumbnail is the optimized webp in the &lt;picture&gt; that sits next to the overlay
    /// under a shared .hovereffect wrapper. The &lt;img&gt; inside that picture is only a
    /// "missing poster" placeholder, so it is deliberately not used as a fallback.
    /// </summary>
    private static string? ExtractThumbnail(HtmlNode card)
    {
        var parent = card.ParentNode;
        var source = parent?.SelectSingleNode(".//source[@type='image/webp'][@srcset]")
                     ?? parent?.SelectSingleNode(".//source[@srcset]");

        var srcset = source?.GetAttributeValue("srcset", null);
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return null;
        }

        // srcset may be "url 1x, url2 2x" — take the first URL.
        var first = srcset.Split(',')[0].Trim().Split(' ')[0];
        return string.IsNullOrWhiteSpace(first) ? null : Absolute(first);
    }

    /// <summary>Card title, e.g. "Inception (2010)" or "Inception - Season 1".</summary>
    private static string? ExtractTitle(HtmlNode card)
    {
        var node = card.SelectSingleNode(".//p[contains(@class, 'text-break')]");
        var text = HtmlEntity.DeEntitize(node?.InnerText ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// TPDB does not expose the artwork kind as an attribute, so it is read off the
    /// card title, which is where seasons and backdrops are labelled.
    /// </summary>
    private static string InferImageType(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "poster";
        }

        if (title.Contains("Season", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Specials", StringComparison.OrdinalIgnoreCase))
        {
            return "season_poster";
        }

        if (title.Contains("Background", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Backdrop", StringComparison.OrdinalIgnoreCase))
        {
            return "backdrop";
        }

        return "poster";
    }

    private static string? ExtractAuthor(HtmlNode card)
    {
        var link = card.ParentNode?.SelectSingleNode(".//a[contains(@href, '/user/')]");
        var text = HtmlEntity.DeEntitize(link?.InnerText ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string Absolute(string href) =>
        href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{BaseUrl}{(href.StartsWith('/') ? string.Empty : "/")}{href}";
}
