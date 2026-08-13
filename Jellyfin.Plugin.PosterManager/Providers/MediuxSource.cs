using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterManager.Providers;

/// <summary>
/// Mediux via its Directus GraphQL API (POST https://images.mediux.io/graphql,
/// "Authorization: Bearer &lt;token&gt;"). Contract mirrors the reference client at
/// github.com/mediux-team/AURA (backend/mediux).
///
/// Unlike TPDB there is no title search: items are resolved strictly by TMDB id, so
/// a Jellyfin item without a TMDB provider id cannot be looked up here.
/// </summary>
public sealed class MediuxSource : IPosterSource
{
    private const string ApiUrl = "https://images.mediux.io";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MediuxSource> _logger;

    // setId -> images, so GetImagesAsync can serve the set the search just fetched.
    // Mediux returns every image inline with the set, so a second round-trip is pure waste.
    private readonly Dictionary<string, IReadOnlyList<PosterImage>> _setCache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    public MediuxSource(IHttpClientFactory httpClientFactory, ILogger<MediuxSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string SourceId => PosterSourceIds.Mediux;

    public string DisplayName => "Mediux";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.MediuxApiToken);

    private HttpClient CreateClient()
    {
        var config = Plugin.Instance!.Configuration;
        var client = _httpClientFactory.CreateClient(nameof(MediuxSource));
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"Bearer {config.MediuxApiToken}");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "jellyfin-poster-manager/1.0");
        return client;
    }

    public async Task<IReadOnlyList<PosterSet>> FindSetsAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.TmdbId))
        {
            _logger.LogInformation(
                "Mediux lookup skipped for {Title}: item has no TMDB id", query.Title);
            return Array.Empty<PosterSet>();
        }

        return query.Kind switch
        {
            ItemKind.Series => await FindShowSetsAsync(query.TmdbId, cancellationToken).ConfigureAwait(false),
            ItemKind.Collection => await FindCollectionSetsAsync(query.TmdbId, cancellationToken).ConfigureAwait(false),
            _ => await FindMovieSetsAsync(query.TmdbId, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// Collection artwork is reached via a member movie's TMDB id (see
    /// <see cref="MediuxQueries.CollectionSetsByMovieTmdbId"/>), so TmdbId here is expected
    /// to be a child movie's id rather than the box set's own.
    /// </summary>
    private async Task<IReadOnlyList<PosterSet>> FindCollectionSetsAsync(string tmdbId, CancellationToken cancellationToken)
    {
        var json = await QueryAsync(MediuxQueries.CollectionSetsByMovieTmdbId, tmdbId, cancellationToken)
            .ConfigureAwait(false);

        var collection = json?["data"]?["movies_by_id"]?["collection_id"];
        if (collection is null)
        {
            return Array.Empty<PosterSet>();
        }

        var setId = collection["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(setId))
        {
            return Array.Empty<PosterSet>();
        }

        var images = new List<PosterImage>();
        foreach (var poster in EnumerateArray(collection["posters"]))
        {
            AddImage(images, poster, "poster", null);
        }

        foreach (var backdrop in EnumerateArray(collection["backdrops"]))
        {
            AddImage(images, backdrop, "backdrop", null);
        }

        if (images.Count == 0)
        {
            return Array.Empty<PosterSet>();
        }

        var cacheKey = "collection:" + setId;
        CacheSet(cacheKey, images);

        return new[]
        {
            new PosterSet
            {
                Id = cacheKey,
                SourceId = SourceId,
                Title = collection["collection_name"]?.GetValue<string>() ?? "Collection",
                Subtitle = $"{images.Count} image{(images.Count == 1 ? string.Empty : "s")}"
            }
        };
    }

    private async Task<IReadOnlyList<PosterSet>> FindMovieSetsAsync(string tmdbId, CancellationToken cancellationToken)
    {
        var json = await QueryAsync(MediuxQueries.MovieSetsByTmdbId, tmdbId, cancellationToken).ConfigureAwait(false);
        var movie = json?["data"]?["movies_by_id"];
        if (movie is null)
        {
            return Array.Empty<PosterSet>();
        }

        var sets = new List<PosterSet>();
        foreach (var set in EnumerateArray(movie["movie_sets"]))
        {
            var setId = set["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(setId))
            {
                continue;
            }

            var author = set["user_created"]?["username"]?.GetValue<string>();
            var images = new List<PosterImage>();
            AddImage(images, set["movie_poster"], "poster", author);
            AddImage(images, set["movie_backdrop"], "backdrop", author);

            if (images.Count == 0)
            {
                continue;
            }

            CacheSet(setId, images);
            sets.Add(new PosterSet
            {
                Id = setId,
                SourceId = SourceId,
                Title = set["set_title"]?.GetValue<string>() ?? "Untitled set",
                Author = author,
                Subtitle = FormatSubtitle(set, images.Count)
            });
        }

        return sets;
    }

    private async Task<IReadOnlyList<PosterSet>> FindShowSetsAsync(string tmdbId, CancellationToken cancellationToken)
    {
        var json = await QueryAsync(MediuxQueries.ShowSetsByTmdbId, tmdbId, cancellationToken).ConfigureAwait(false);
        var show = json?["data"]?["shows_by_id"];
        if (show is null)
        {
            return Array.Empty<PosterSet>();
        }

        var sets = new List<PosterSet>();
        foreach (var set in EnumerateArray(show["show_sets"]))
        {
            var setId = set["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(setId))
            {
                continue;
            }

            var author = set["user_created"]?["username"]?.GetValue<string>();
            var images = new List<PosterImage>();
            AddImage(images, set["show_poster"], "poster", author);
            AddImage(images, set["show_backdrop"], "backdrop", author);

            foreach (var seasonPoster in EnumerateArray(set["season_posters"]))
            {
                AddImage(images, seasonPoster, "season_poster", author);
            }

            foreach (var titlecard in EnumerateArray(set["titlecards"]))
            {
                AddImage(images, titlecard, "titlecard", author);
            }

            if (images.Count == 0)
            {
                continue;
            }

            CacheSet(setId, images);
            sets.Add(new PosterSet
            {
                Id = setId,
                SourceId = SourceId,
                Title = set["set_title"]?.GetValue<string>() ?? "Untitled set",
                Author = author,
                Subtitle = FormatSubtitle(set, images.Count)
            });
        }

        return sets;
    }

    public Task<IReadOnlyList<PosterImage>> GetImagesAsync(string setId, ItemKind kind, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_setCache.TryGetValue(setId, out var cached))
            {
                return Task.FromResult(cached);
            }
        }

        // The client always lists sets before opening one, so a miss means the cache was
        // evicted or the server restarted; the user just needs to search again.
        _logger.LogInformation("Mediux set {SetId} not in cache; re-run the search", setId);
        return Task.FromResult<IReadOnlyList<PosterImage>>(Array.Empty<PosterImage>());
    }

    public async Task<(Stream Stream, string ContentType)> DownloadAsync(string imageUrl, CancellationToken cancellationToken)
    {
        if (!IsTrustedUrl(imageUrl))
        {
            throw new InvalidOperationException($"Refusing to download non-Mediux URL: {imageUrl}");
        }

        using var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/*");

        var response = await client.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return (new MemoryStream(bytes), contentType);
    }

    internal static bool IsTrustedUrl(string imageUrl) =>
        Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("images.mediux.io", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("api.mediux.pro", StringComparison.OrdinalIgnoreCase));

    private async Task<System.Text.Json.Nodes.JsonNode?> QueryAsync(
        string query, string tmdbId, CancellationToken cancellationToken)
    {
        using var client = CreateClient();

        var body = new GraphQlRequest
        {
            Query = query,
            Variables = new Dictionary<string, object> { ["tmdb_id"] = tmdbId }
        };

        var response = await client.PostAsJsonAsync($"{ApiUrl}/graphql", body, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "Mediux rejected the API token. Check MediuxApiToken in the plugin settings.");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var json = System.Text.Json.Nodes.JsonNode.Parse(payload);

        // GraphQL reports failures in an "errors" array, with HTTP 200 or 400.
        var errors = json?["errors"];
        if (errors is System.Text.Json.Nodes.JsonArray { Count: > 0 } errorArray)
        {
            var message = errorArray[0]?["message"]?.GetValue<string>() ?? "unknown error";

            // An unauthenticated request doesn't get a 401 — the schema simply hides the
            // query fields, so the token problem shows up as a validation error instead.
            if (message.Contains("Cannot query field", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Mediux did not accept the API token (the query fields are not visible). "
                    + "Check MediuxApiToken in the plugin settings.");
            }

            throw new InvalidOperationException($"Mediux API error: {message}");
        }

        response.EnsureSuccessStatusCode();
        return json;
    }

    private void CacheSet(string setId, IReadOnlyList<PosterImage> images)
    {
        lock (_cacheLock)
        {
            _setCache[setId] = images;
        }
    }

    /// <summary>
    /// Mediux serves assets from /assets{src}, where src already carries its own
    /// "?v=..." (e.g. "/e24cf01d-...?v=20240115174748"); "&amp;key=thumb" yields the
    /// thumbnail and "&amp;key=jpg" the optimized copy. A src prefixed with "---" is a
    /// manual import served from the public host instead.
    /// </summary>
    private static void AddImage(
        List<PosterImage> images,
        System.Text.Json.Nodes.JsonNode? node,
        string imageType,
        string? author)
    {
        if (node is null)
        {
            return;
        }

        // Movie poster/backdrop fields come back as single-element arrays.
        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var element in array)
            {
                AddImage(images, element, imageType, author);
            }

            return;
        }

        var src = node["src"]?.GetValue<string>();
        var id = node["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var optimized = string.Equals(
            Plugin.Instance?.Configuration.MediuxDownloadQuality,
            "optimized",
            StringComparison.OrdinalIgnoreCase);

        var image = new PosterImage
        {
            Id = id,
            SourceId = PosterSourceIds.Mediux,
            ImageType = imageType,
            Author = author,
            // language is null on most assets; Mediux treats those as English.
            Language = node["language"]?["display_name"]?.GetValue<string>() ?? "English",
            ThumbnailUrl = BuildAssetUrl(src, "thumb"),
            FullUrl = BuildAssetUrl(src, optimized ? "jpg" : null)
        };

        var seasonNumber = node["season"]?["season_number"]?.GetValue<int?>();
        var episode = node["episode"];
        if (episode is not null)
        {
            image.Title = episode["episode_title"]?.GetValue<string>();
            image.EpisodeNumber = episode["episode_number"]?.GetValue<int?>();
            seasonNumber ??= episode["season_id"]?["season_number"]?.GetValue<int?>();
        }

        image.SeasonNumber = seasonNumber;
        images.Add(image);
    }

    /// <summary>
    /// src arrives as "/{uuid}?v={version}" — already rooted and already versioned — so it
    /// is appended verbatim and any quality key joins with "&amp;", not "?".
    /// </summary>
    private static string BuildAssetUrl(string src, string? key)
    {
        var host = ApiUrl;
        if (src.StartsWith("---", StringComparison.Ordinal))
        {
            host = "https://api.mediux.pro";
            src = src[3..];
            key = null; // manual imports are only served at original quality
        }

        if (!src.StartsWith('/'))
        {
            src = "/" + src;
        }

        var url = $"{host}/assets{src}";
        if (key is null)
        {
            return url;
        }

        return url + (src.Contains('?', StringComparison.Ordinal) ? "&" : "?") + $"key={key}";
    }

    private static string FormatSubtitle(System.Text.Json.Nodes.JsonNode set, int imageCount)
    {
        var popularity = set["popularity_global"]?.GetValue<int?>() ?? set["popularity"]?.GetValue<int?>();
        var parts = new List<string> { $"{imageCount} image{(imageCount == 1 ? string.Empty : "s")}" };
        if (popularity is > 0)
        {
            parts.Add($"★ {popularity}");
        }

        return string.Join(" · ", parts);
    }

    private static IEnumerable<System.Text.Json.Nodes.JsonNode> EnumerateArray(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var element in array)
            {
                if (element is not null)
                {
                    yield return element;
                }
            }
        }
    }

    private sealed class GraphQlRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("variables")]
        public Dictionary<string, object> Variables { get; set; } = new();
    }
}
