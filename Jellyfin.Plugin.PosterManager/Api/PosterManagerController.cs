using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.PosterManager.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterManager.Api;

[ApiController]
[Authorize]
[Route("PosterManager")]
public class PosterManagerController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IEnumerable<IPosterSource> _sources;
    private readonly ILogger<PosterManagerController> _logger;

    public PosterManagerController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IEnumerable<IPosterSource> sources,
        ILogger<PosterManagerController> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _sources = sources;
        _logger = logger;
    }

    /// <summary>Which sources are usable right now, for the client's tab strip.</summary>
    [HttpGet("Sources")]
    public ActionResult<IEnumerable<object>> GetSources() =>
        Ok(_sources.Select(s => new
        {
            id = s.SourceId,
            name = s.DisplayName,
            configured = s.IsConfigured
        }));

    /// <summary>
    /// Item context for the dialog: the title to pre-fill the search box with and the
    /// TMDB id Mediux needs. Local titles often differ from the sites' English titles,
    /// which is why the client lets the user edit the term.
    /// </summary>
    [HttpGet("Item/{id}")]
    public ActionResult<object> GetItem([FromRoute] Guid id)
    {
        var item = _libraryManager.GetItemById(id);
        if (item is null)
        {
            return NotFound();
        }

        return Ok(new
        {
            id = item.Id.ToString("N"),
            name = item.Name,
            year = item.ProductionYear,
            kind = ResolveKind(item).ToString().ToLowerInvariant(),
            tmdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb),
            tvdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tvdb)
        });
    }

    /// <summary>Find candidate sets on one source for the given item.</summary>
    [HttpGet("Search")]
    public async Task<ActionResult<IEnumerable<PosterSet>>> Search(
        [FromQuery] Guid itemId,
        [FromQuery] string source,
        [FromQuery] string? term,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { error = "Item not found." });
        }

        var posterSource = ResolveSource(source);
        if (posterSource is null)
        {
            return BadRequest(new { error = $"Unknown source '{source}'." });
        }

        if (!posterSource.IsConfigured)
        {
            return BadRequest(new { error = $"{posterSource.DisplayName} is not configured in the plugin settings." });
        }

        var kind = ResolveKind(item);

        var query = new ItemQuery
        {
            Title = string.IsNullOrWhiteSpace(term) ? item.Name : term,
            Year = item.ProductionYear,
            Kind = kind,
            TmdbId = kind == ItemKind.Collection
                ? ResolveCollectionTmdbId(item)
                : item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb),
            TvdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tvdb)
        };

        try
        {
            var sets = await posterSource.FindSetsAsync(query, cancellationToken).ConfigureAwait(false);
            return Ok(sets);
        }
        catch (InvalidOperationException ex)
        {
            // Config problems (bad token, stale Cloudflare cookie) — surface the text to the user.
            _logger.LogWarning(ex, "{Source} search failed", posterSource.DisplayName);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>List the images inside one set.</summary>
    [HttpGet("Posters")]
    public async Task<ActionResult<IEnumerable<PosterImage>>> GetPosters(
        [FromQuery] Guid itemId,
        [FromQuery] string source,
        [FromQuery] string setId,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { error = "Item not found." });
        }

        var posterSource = ResolveSource(source);
        if (posterSource is null)
        {
            return BadRequest(new { error = $"Unknown source '{source}'." });
        }

        try
        {
            var images = await posterSource
                .GetImagesAsync(setId, ResolveKind(item), cancellationToken)
                .ConfigureAwait(false);
            return Ok(images);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "{Source} poster listing failed", posterSource.DisplayName);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Proxy a thumbnail through the server. TPDB needs the Cloudflare cookie and Mediux
    /// needs the bearer token, neither of which the browser has — so the grid can't load
    /// these URLs directly.
    /// </summary>
    [HttpGet("Thumbnail")]
    public async Task<IActionResult> GetThumbnail(
        [FromQuery] string source,
        [FromQuery] string url,
        CancellationToken cancellationToken)
    {
        var posterSource = ResolveSource(source);
        if (posterSource is null)
        {
            return BadRequest();
        }

        try
        {
            var (stream, contentType) = await posterSource.DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            return File(stream, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail proxy failed for {Url}", url);
            return NotFound();
        }
    }

    /// <summary>Download the chosen image and save it onto the item.</summary>
    [HttpPost("Apply")]
    public async Task<IActionResult> Apply([FromBody] ApplyRequest request, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound(new { error = "Item not found." });
        }

        var posterSource = ResolveSource(request.Source);
        if (posterSource is null)
        {
            return BadRequest(new { error = $"Unknown source '{request.Source}'." });
        }

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new { error = "No image URL supplied." });
        }

        try
        {
            var (stream, contentType) = await posterSource
                .DownloadAsync(request.Url, cancellationToken)
                .ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                var imageType = MapImageType(request.ImageType);

                await _providerManager
                    .SaveImage(item, stream, contentType, imageType, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new { applied = true });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Apply failed for item {ItemId}", request.ItemId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Serves the injected client script. Anonymous because the browser requests it as a
    /// plain &lt;script src&gt; before any auth header can be attached.
    /// </summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = $"{typeof(Plugin).Namespace}.Web.postermanager.js";

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    private IPosterSource? ResolveSource(string? sourceId) =>
        _sources.FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mediux reaches collection artwork through a member movie's TMDB id — TMDB collection
    /// ids are a separate namespace, and a box set often has no TMDB id of its own. So take
    /// the first child movie that has one.
    /// </summary>
    private string? ResolveCollectionTmdbId(BaseItem item)
    {
        if (item is not BoxSet boxSet)
        {
            return item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        }

        foreach (var child in boxSet.GetRecursiveChildren())
        {
            if (child is not Movie)
            {
                continue;
            }

            var tmdbId = child.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                return tmdbId;
            }
        }

        return null;
    }

    private static ItemKind ResolveKind(BaseItem item) => item switch
    {
        Series => ItemKind.Series,
        BoxSet => ItemKind.Collection,
        _ => ItemKind.Movie
    };

    private static ImageType MapImageType(string? imageType) =>
        imageType?.ToLowerInvariant() switch
        {
            "backdrop" => ImageType.Backdrop,
            "banner" => ImageType.Banner,
            "logo" => ImageType.Logo,
            "thumb" or "titlecard" => ImageType.Thumb,
            _ => ImageType.Primary
        };

    public sealed class ApplyRequest
    {
        public Guid ItemId { get; set; }

        public string Source { get; set; } = string.Empty;

        /// <summary>Full-resolution URL, as returned by the Posters endpoint.</summary>
        public string Url { get; set; } = string.Empty;

        public string? ImageType { get; set; }
    }
}
