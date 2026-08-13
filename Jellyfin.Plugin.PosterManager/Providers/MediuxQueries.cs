namespace Jellyfin.Plugin.PosterManager.Providers;

/// <summary>
/// GraphQL documents for the Mediux Directus API, kept in sync with the reference
/// client at github.com/mediux-team/AURA (backend/mediux/*.graphql).
/// </summary>
internal static class MediuxQueries
{
    public const string MovieSetsByTmdbId = """
        query getMovieItemSetsByTMDBID($tmdb_id: ID!) {
          movies_by_id(id: $tmdb_id) {
            id
            date_updated
            status
            title
            release_date
            tvdb_id
            imdb_id
            slug
            movie_sets(
              filter: {
                _or: [
                  { movie_poster: { id: { _neq: null } } }
                  { movie_backdrop: { id: { _neq: null } } }
                ]
              }
            ) {
              id
              set_title
              user_created { username }
              date_created
              date_updated
              popularity
              popularity_global
              movie_poster {
                id
                modified_on
                filesize
                src
                language { display_name }
              }
              movie_backdrop {
                id
                modified_on
                filesize
                src
                language { display_name }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Collections are reached through a *member movie's* TMDB id — TMDB collection ids
    /// live in a different namespace, and movies_by_id would silently resolve one to an
    /// unrelated movie. "posters" on the collection are collection-level artwork; the
    /// per-movie "posters" carry the collection_set each belongs to.
    /// </summary>
    public const string CollectionSetsByMovieTmdbId = """
        query getMovieItemCollectionSetsByTMDBID($tmdb_id: ID!) {
          movies_by_id(id: $tmdb_id) {
            collection_id {
              id
              collection_name
              posters {
                id
                modified_on
                filesize
                src
              }
              backdrops {
                id
                modified_on
                filesize
                src
              }
            }
          }
        }
        """;

    public const string ShowSetsByTmdbId = """
        query getShowItemSetsByTMDBID($tmdb_id: ID!) {
          shows_by_id(id: $tmdb_id) {
            id
            date_updated
            status
            title
            first_air_date
            tvdb_id
            imdb_id
            slug
            show_sets(
              filter: {
                _or: [
                  { show_poster: { id: { _nnull: true } } }
                  { show_backdrop: { id: { _nnull: true } } }
                  { season_posters: { id: { _nnull: true } } }
                  { titlecards: { id: { _nnull: true } } }
                ]
              }
            ) {
              id
              set_title
              user_created { username }
              date_created
              date_updated
              popularity
              popularity_global
              show_poster {
                id
                modified_on
                filesize
                src
                language { display_name }
              }
              show_backdrop {
                id
                modified_on
                filesize
                src
                language { display_name }
              }
              season_posters(filter: { season: { season_number: { _nnull: true } } }) {
                season { season_number }
                id
                modified_on
                filesize
                src
                language { display_name }
              }
              titlecards(
                filter: {
                  episode: {
                    episode_number: { _nnull: true }
                    season_id: { season_number: { _nnull: true } }
                  }
                }
              ) {
                id
                modified_on
                filesize
                src
                language { display_name }
                episode {
                  episode_title
                  episode_number
                  season_id { season_number }
                }
              }
            }
          }
        }
        """;
}
