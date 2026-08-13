namespace Jellyfin.Plugin.PosterManager.Providers;

/// <summary>Which upstream a set/poster came from.</summary>
public static class PosterSourceIds
{
    public const string ThePosterDb = "tpdb";
    public const string Mediux = "mediux";
}

/// <summary>The kind of Jellyfin item we are looking for artwork for.</summary>
public enum ItemKind
{
    Movie,
    Series,
    Collection
}

/// <summary>
/// What a source needs in order to look an item up. TPDB matches on title;
/// Mediux is keyed strictly by TMDB id.
/// </summary>
public sealed class ItemQuery
{
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? TmdbId { get; set; }

    public string? TvdbId { get; set; }

    public ItemKind Kind { get; set; }
}

/// <summary>A candidate group of posters (a TPDB poster page, or a Mediux set).</summary>
public sealed class PosterSet
{
    public string Id { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Author { get; set; }

    public int? Year { get; set; }

    /// <summary>Free-form extra line shown in the picker (e.g. popularity).</summary>
    public string? Subtitle { get; set; }
}

/// <summary>A single applicable image.</summary>
public sealed class PosterImage
{
    public string Id { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    /// <summary>Thumbnail URL proxied to the browser for the grid.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Full-resolution URL the server downloads when applying.</summary>
    public string FullUrl { get; set; } = string.Empty;

    /// <summary>poster | backdrop | season_poster | titlecard</summary>
    public string ImageType { get; set; } = "poster";

    public string? Language { get; set; }

    public string? Author { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? Title { get; set; }
}
