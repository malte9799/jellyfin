namespace Jellyfin.Plugin.PosterManager.Providers;

/// <summary>
/// One upstream artwork site. Sources differ in how they find an item — TPDB does a
/// title search that may need disambiguation, Mediux resolves directly from a TMDB id —
/// so both steps stay separate.
/// </summary>
public interface IPosterSource
{
    /// <summary>Stable id used on the wire, see <see cref="PosterSourceIds"/>.</summary>
    string SourceId { get; }

    /// <summary>Display name shown on the source tab.</summary>
    string DisplayName { get; }

    /// <summary>False when the source is missing required configuration (token/cookie).</summary>
    bool IsConfigured { get; }

    /// <summary>Find candidate sets for an item.</summary>
    Task<IReadOnlyList<PosterSet>> FindSetsAsync(ItemQuery query, CancellationToken cancellationToken);

    /// <summary>List the images inside one set.</summary>
    Task<IReadOnlyList<PosterImage>> GetImagesAsync(string setId, ItemKind kind, CancellationToken cancellationToken);

    /// <summary>Download a full-resolution image. Returns the stream and its content type.</summary>
    Task<(Stream Stream, string ContentType)> DownloadAsync(string imageUrl, CancellationToken cancellationToken);
}
