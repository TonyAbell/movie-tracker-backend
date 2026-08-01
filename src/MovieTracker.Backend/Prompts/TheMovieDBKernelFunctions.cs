using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Text.Json;
using TMDbLib.Client;
using TMDbLib.Objects.Discover;

namespace MovieTracker.Backend.Prompts
{
    public record MovieSearchResult(string MovieId, string MovieName, string ReleaseDate, string ImdbId);
    public record GenresItem(string GenreId, string GenreName);

    public class TheMovieDBKernelFunctions(TMDbClient client)
    {
        /// <summary>
        /// How many TMDb calls the id fan-out below is allowed to have in flight at once. TMDb rate
        /// limits, and a search can return twenty results, so this is deliberately not unbounded.
        /// </summary>
        private const int MaxConcurrentTmdbCalls = 8;

        /// <summary>
        /// Resolves the IMDb id for each search hit and projects the pair into a MovieSearchResult.
        /// <para>
        /// TMDb has no batch endpoint for external ids, so this is one call per result either way - but
        /// it used to be one call per result <i>sequentially</i>, inside a foreach, in both list-returning
        /// tools. That put up to twenty round trips on the critical path of a single tool call: measured
        /// in production at 0.91s mean for SearchMovies and 0.99s for DiscoverMovies, which is what ~21
        /// serial ~40ms calls costs. Running them concurrently is the whole difference.
        /// </para>
        /// <para>
        /// Results are written by index rather than appended, so the order TMDb ranked them in survives.
        /// </para>
        /// </summary>
        private static async Task<List<MovieSearchResult>> ToSearchResultsAsync<T>(
            IReadOnlyList<T> movies,
            Func<T, int> getId,
            Func<T, string?> getTitle,
            Func<T, DateTime?> getReleaseDate,
            Func<int, Task<string?>> getImdbId)
        {
            var results = new MovieSearchResult[movies.Count];
            using var throttle = new SemaphoreSlim(MaxConcurrentTmdbCalls);

            await Task.WhenAll(movies.Select(async (movie, index) =>
            {
                await throttle.WaitAsync();
                try
                {
                    string imdbId;
                    try
                    {
                        imdbId = await getImdbId(getId(movie)) ?? "";
                    }
                    catch
                    {
                        // One unresolvable external id must not fail the whole lookup; the model can
                        // still use the MovieId, and DescribeMovie will surface the IMDb id if needed.
                        imdbId = "";
                    }

                    results[index] = new MovieSearchResult(
                        getId(movie).ToString(),
                        getTitle(movie) ?? "",
                        getReleaseDate(movie)?.ToString("yyyy-MM-dd") ?? "",
                        imdbId);
                }
                finally
                {
                    throttle.Release();
                }
            }));

            return [.. results];
        }

        /// <summary>
        /// The Agent Framework has no equivalent of SK's Plugins.AddFromType&lt;T&gt;(), which discovered
        /// methods by their [KernelFunction] attribute. Tools are now an explicit list, so this method
        /// is the single place that decides which methods the model can call. Adding a method below
        /// without adding it here leaves it invisible to the model.
        /// [Description] attributes still drive the schema handed to the model, exactly as before.
        /// </summary>
        /// <remarks>
        /// Three methods are deliberately not offered:
        /// <list type="bullet">
        /// <item><description>
        /// <see cref="GetMovieDetails"/> and <see cref="GetMovieWithTrailer"/> are the same
        /// GetMovieAsync lookup as <see cref="DescribeMovie"/> under different names, projecting
        /// overlapping but inconsistent field sets - Genres as objects in one and bare strings in
        /// another, VoteAverage in one and TmdbVoteAverage in another. Three descriptions for one
        /// capability is an ambiguous choice for the model to get wrong, which is exactly why
        /// GetMovieRatingGeneric was withdrawn from ChatPlanner. DescribeMovie absorbed the fields the
        /// other two carried, so nothing is lost. Both methods are kept - they cost nothing unregistered
        /// and are the obvious starting point if a detail tool ever needs to be split again.
        /// </description></item>
        /// <item><description>
        /// <see cref="HandleGenericTrailerRequest"/> makes no TMDb call at all - it ignores its argument
        /// and returns a fixed "which movie did you mean?" string. That is a sentence the model can
        /// write itself, bought at the price of a tool schema in every request and a round trip.
        /// </description></item>
        /// </list>
        /// Every tool schema rides in the cached prompt prefix on every single call, so dropping three
        /// is a permanent token saving as well as three fewer wrong turns available to the model.
        /// </remarks>
        public IEnumerable<AITool> CreateTools() =>
        [
            AIFunctionFactory.Create(GetGenresList),
            AIFunctionFactory.Create(SearchForPeople),
            AIFunctionFactory.Create(SearchMovies),
            AIFunctionFactory.Create(GetMovieTrailers),
            AIFunctionFactory.Create(SearchKeywords),
            AIFunctionFactory.Create(DescribeMovie),
            AIFunctionFactory.Create(DiscoverMovies),
        ];

        /// <summary>
        /// The model frequently invents ids like "1990-Joe-Versus-the-Volcano" instead of reusing
        /// one returned by a search. Throwing on those aborts the whole chat turn, so instead hand
        /// the model a description of what it did wrong and let it correct itself.
        /// </summary>
        private static bool TryGetTmdbId(string? movieId, out int id, out string error)
        {
            if (int.TryParse(movieId, out id))
            {
                error = string.Empty;
                return true;
            }

            error = JsonSerializer.Serialize(new
            {
                Error = $"'{movieId}' is not a valid TMDb movie id. Ids are numeric, e.g. '13'.",
                Hint = "Call SearchMovies or DiscoverMovies first and use the MovieId it returns."
            });
            return false;
        }

        /// <summary>
        /// Parses a model-supplied comma separated id list, dropping anything non-numeric
        /// (the model sometimes passes names rather than ids).
        /// </summary>
        private static List<int> ParseIdList(string ids) =>
            ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(value => int.TryParse(value, out var parsed) ? parsed : (int?)null)
               .Where(parsed => parsed.HasValue)
               .Select(parsed => parsed!.Value)
               .ToList();

        [Description("Get the list of official genres for movies.")]
        [return: Description("a json list of official genres for movies, with the following properties GenreId and the GenreName")]
        public async Task<string> GetGenresList()
        {
            var genres = await client.GetMovieGenresAsync();
            var genresList = genres.Select(g => new GenresItem(g.Id.ToString(), g.Name)).ToList();
            return JsonSerializer.Serialize(genresList);
        }

        public record PersonSearchResult(string PersonId, string PersonName);
        [Description("Search for people / cast by their name and also known as names.")]
        [return: Description("a json list of people with the following properties PersonId and the PersonName")]
        public async Task<string> SearchForPeople(
                 [Description("The name of the person or cast member")] string personName)
        {
            // No region argument: the parameter is ISO-3166-1 (a country, "US"), and what used to be
            // passed was "en-US" - a language tag in a country slot, which TMDb ignores at best.
            var searchResults = await client.SearchPersonAsync(personName, includeAdult: false);
            var personSearchResults = searchResults.Results.Select(p => new PersonSearchResult(p.Id.ToString(), p.Name)).ToList();
            return JsonSerializer.Serialize(personSearchResults);
        }

        [Description("Search for movies by their title and release year. Use this to find movies, you can search by movie name or part of a movie name")]
        [return: Description("a json list of movies with the following properties MovieId, MovieName, ReleaseDate, and ImdbId")]
        public async Task<string> SearchMovies(
            [Description("The title of the movie, or part of the title")] string movieTitle,
            [Description("Optional: The year the movie was released")] string? releaseYear = null)
        {
            // The model sometimes passes values like "1990s" or "mid-90s"; treat those as unset.
            _ = int.TryParse(releaseYear, out var yearAsInt);
            var searchResults = await client.SearchMovieAsync(movieTitle, year: yearAsInt);

            var movieSearchResults = await ToSearchResultsAsync(
                searchResults.Results,
                movie => movie.Id,
                movie => movie.Title,
                movie => movie.ReleaseDate,
                async id => (await client.GetMovieExternalIdsAsync(id)).ImdbId);

            return JsonSerializer.Serialize(movieSearchResults);
        }

        [Description("Get movie trailers, teasers, video clips, behind-the-scenes content, and interviews for a specific movie. Use this when users ask to 'show trailer', 'play trailer', 'watch video', 'preview movie', 'see teaser', 'video content', 'behind-the-scenes', or any video-related requests for a movie.")]
        [return: Description("JSON object containing all available video content including trailers, teasers, clips, and behind-the-scenes footage")]
        public async Task<string> GetMovieTrailers(
        [Description("The TMDb movie ID")] string movieId)
        {
            if (!TryGetTmdbId(movieId, out var tmdbId, out var idError)) return idError;

            // One request, not two. append_to_response folds the videos into the movie payload, and
            // this method needed both anyway - it was fetching the videos, partitioning them, and then
            // making a second round trip purely for the title and year.
            var movie = await client.GetMovieAsync(tmdbId, TMDbLib.Objects.Movies.MovieMethods.Videos);

            var allVideos = movie.Videos.Results
                .Where(v => v.Site == "YouTube")
                .Select(v => new
                {
                    Name = v.Name,
                    Type = v.Type, // "Trailer", "Teaser", "Clip", "Behind the Scenes", "Featurette"
                    Key = v.Key,
                    YouTubeUrl = $"https://www.youtube.com/watch?v={v.Key}",
                    EmbedUrl = $"https://www.youtube.com/embed/{v.Key}",
                    ThumbnailUrl = $"https://img.youtube.com/vi/{v.Key}/maxresdefault.jpg",
                    Official = v.Official
                })
                .ToList();

            var trailers = allVideos.Where(v => v.Type == "Trailer").ToList();
            var teasers = allVideos.Where(v => v.Type == "Teaser").ToList();
            var clips = allVideos.Where(v => v.Type == "Clip").ToList();
            var behindScenes = allVideos.Where(v => v.Type == "Behind the Scenes").ToList();
            var featurettes = allVideos.Where(v => v.Type == "Featurette").ToList();

            return JsonSerializer.Serialize(new
            {
                MovieTitle = movie.Title,
                MovieYear = movie.ReleaseDate?.Year,
                TotalVideoCount = allVideos.Count,
                MainTrailer = trailers.FirstOrDefault(t => t.Official) ?? trailers.FirstOrDefault() ?? allVideos.FirstOrDefault(),
                Videos = new
                {
                    Trailers = trailers,
                    Teasers = teasers,
                    Clips = clips,
                    BehindTheScenes = behindScenes,
                    Featurettes = featurettes
                },
                Summary = allVideos.Any()
                    ? $"Found {allVideos.Count} video(s) for {movie.Title} including trailers, clips, and behind-the-scenes content"
                    : $"No video content available for {movie.Title}"
            });
        }

        [Description("Get movie information with trailer included for inline chat display. Use when users ask to 'show movie', 'tell me about movie', or want general movie info that should include a trailer preview.")]
        [return: Description("Complete movie information with embedded trailer for chat display")]
        public async Task<string> GetMovieWithTrailer(
            [Description("The TMDb movie ID")] string movieId)
        {
            if (!TryGetTmdbId(movieId, out var tmdbId, out var idError)) return idError;

            var movie = await client.GetMovieAsync(tmdbId, TMDbLib.Objects.Movies.MovieMethods.Videos);

            var trailer = movie.Videos.Results
                .Where(v => v.Type == "Trailer" && v.Site == "YouTube")
                .OrderByDescending(v => v.Official)
                .FirstOrDefault();

            return JsonSerializer.Serialize(new
            {
                MovieId = movieId,
                Title = movie.Title,
                Overview = movie.Overview,
                ReleaseDate = movie.ReleaseDate?.ToString("yyyy-MM-dd"),
                ImdbId = movie.ImdbId ?? "",
                DisplayType = "movie-with-inline-trailer",
                Trailer = trailer != null ? new
                {
                    HasTrailer = true,
                    Name = trailer.Name,
                    YouTubeUrl = $"https://www.youtube.com/watch?v={trailer.Key}",
                    EmbedUrl = $"https://www.youtube.com/embed/{trailer.Key}",
                    ThumbnailUrl = $"https://img.youtube.com/vi/{trailer.Key}/maxresdefault.jpg",
                    DisplayInline = true,
                    AllowFullScreen = true
                } : new
                {
                    HasTrailer = false,
                    Name = "No trailer available",
                    YouTubeUrl = "",
                    EmbedUrl = "",
                    ThumbnailUrl = "",
                    DisplayInline = false,
                    AllowFullScreen = false
                },
                ChatMessage = trailer != null
                    ? "Here's the movie info with trailer - tap to watch full screen!"
                    : "Here's the movie info (no trailer available)"
            });
        }

        [Description("Handle generic video/trailer requests when context is unclear. Use for queries like 'trailer please', 'play trailer', 'watch video', 'movie trailer?' when no specific movie is mentioned.")]
        [return: Description("Response asking for clarification about which movie trailer they want")]
        public async Task<string> HandleGenericTrailerRequest(
            [Description("The user's generic trailer request")] string userQuery)
        {
            return JsonSerializer.Serialize(new
            {
                Type = "clarification-needed",
                Message = "I'd be happy to show you a trailer! Which movie are you interested in?",
                Suggestions = new[]
                {
            "Try: 'Show me the Inception trailer'",
            "Or: 'Play the Batman trailer'",
            "Or: 'Trailer for Top Gun Maverick'"
        },
                FollowUp = "Just tell me the movie name and I'll find the trailer for you!"
            });
        }

        [Description("Get detailed information about a specific movie by its ID.")]
        [return: Description("Detailed information about the movie, including title, overview, release date, genres, runtime, and ImdbId.")]
        public async Task<string> GetMovieDetails(
        [Description("The ID of the movie")] string movieId)
        {
            if (!TryGetTmdbId(movieId, out var tmdbId, out var idError)) return idError;

            var movie = await client.GetMovieAsync(tmdbId);

            var movieDetails = new
            {
                MovieId = movieId,
                Title = movie.Title,
                Overview = movie.Overview,
                ReleaseDate = movie.ReleaseDate?.ToString("yyyy-MM-dd"),
                Genres = movie.Genres.Select(g => new { Id = g.Id, Name = g.Name }).ToList(),
                Runtime = movie.Runtime,
                // Same reasoning as DescribeMovie below. This one was missed when that rename went in,
                // which left a bare "Rating"-shaped field in a registered tool and made the system
                // prompt's claim - "TMDb search and detail results carry a TmdbVoteAverage" - untrue
                // for half the detail tools.
                TmdbVoteAverage = movie.VoteAverage,
                TmdbVoteCount = movie.VoteCount,
                ImdbId = movie.ImdbId ?? "",
                PosterPath = movie.PosterPath,
                BackdropPath = movie.BackdropPath
            };

            return JsonSerializer.Serialize(movieDetails);
        }

        [Description("Search for keywords related to movies.")]
        [return: Description("A JSON list of keywords with their properties such as KeywordId and Name.")]
        public async Task<string> SearchKeywords(
        [Description("The name or partial name of the keyword")] string keyword)
        {
            var keywords = await client.SearchKeywordAsync(keyword);
            var keywordList = keywords.Results.Select(k => new { KeywordId = k.Id, Name = k.Name }).ToList();
            return JsonSerializer.Serialize(keywordList);
        }

        [Description("Get full details for one movie by its TMDb MovieId: overview, release date, genres " +
                     "(with ids), runtime, tagline, language, top-billed cast, poster art and the ImdbId. " +
                     "This is the single movie-detail tool - use it whenever you need more about a film " +
                     "than a search result carries. For the rating, call GetMovieRating with the ImdbId " +
                     "this returns.")]
        [return: Description("Serialized JSON containing information about a specific movie including ImdbId")]
        public async Task<string> DescribeMovie([Description("The movie ID of a specific movie")] string movieId)
        {
            if (!TryGetTmdbId(movieId, out var tmdbId, out var idError)) return idError;

            // Credits appended rather than fetched separately - this was the app's only remaining
            // two-call detail lookup.
            var movie = await client.GetMovieAsync(tmdbId, TMDbLib.Objects.Movies.MovieMethods.Credits);

            var movieData = new
            {
                MovieId = movieId,
                Title = movie.Title,
                Overview = movie.Overview,
                ReleaseDate = movie.ReleaseDate?.ToString("yyyy-MM-dd"),
                // Ids as well as names, absorbed from GetMovieDetails: they are what DiscoverMovies
                // takes, so "more like this one" does not need a second GetGenresList round trip.
                Genres = movie.Genres.Select(g => new { Id = g.Id, Name = g.Name }).ToList(),
                Runtime = movie.Runtime,
                Tagline = movie.Tagline,
                // Named for what it is. Called "Rating" this reads as *the* rating, and the model
                // would answer "which had the highest rating?" straight from it instead of calling
                // GetMovieRating - which is the IMDb-backed answer this app treats as the default.
                TmdbVoteAverage = movie.VoteAverage,
                TmdbVoteCount = movie.VoteCount,
                Language = movie.OriginalLanguage,
                ImdbId = movie.ImdbId ?? "",
                PosterPath = movie.PosterPath,
                BackdropPath = movie.BackdropPath,
                Cast = movie.Credits?.Cast?.Take(5).Select(c => c.Name).ToList() // Top 5 cast members
            };

            // Not WriteIndented. This was the only payload in the file formatted that way, and the
            // whitespace is pure token cost on the one tool that already returns the most fields.
            return JsonSerializer.Serialize(movieData);
        }

        /// <summary>
        /// Maps the small vocabulary the model is given to TMDb's sort keys. Anything unrecognised
        /// leaves the sort unset rather than throwing - same contract as <see cref="TryGetTmdbId"/>,
        /// since an unknown sort is a degraded answer and an exception is no answer at all.
        /// </summary>
        private static DiscoverMovieSortBy? ParseSortBy(string? sortBy) =>
            sortBy?.Trim().ToLowerInvariant() switch
            {
                "popularity" => DiscoverMovieSortBy.PopularityDesc,
                "rating" or "vote_average" => DiscoverMovieSortBy.VoteAverageDesc,
                "newest" or "release_date" => DiscoverMovieSortBy.PrimaryReleaseDateDesc,
                "oldest" => DiscoverMovieSortBy.PrimaryReleaseDate,
                "revenue" => DiscoverMovieSortBy.RevenueDesc,
                "votes" or "vote_count" => DiscoverMovieSortBy.VoteCountDesc,
                _ => null,
            };

        /// <summary>
        /// TMDb ranks by raw average, so sorting by rating with no popularity floor surfaces obscure
        /// films carrying a single 10/10 vote ahead of everything the user has heard of. Applied only
        /// when the caller did not set its own floor.
        /// </summary>
        private const int RatingSortMinimumVotes = 200;

        [Description("Discover movies matching filters, optionally sorted. This is the tool for " +
                     "'best', 'top rated', 'most popular', 'highest grossing' and 'newest' questions - " +
                     "pass sortBy rather than sorting the results yourself.")]
        [return: Description("A JSON list of movies with their properties such as MovieId, MovieName, ReleaseDate, and ImdbId.")]
        public async Task<string> DiscoverMovies(
          [Description("Optional: Start release date (YYYY-MM-DD)")] string? releaseDateFrom = null,
          [Description("Optional: End release date (YYYY-MM-DD)")] string? releaseDateTo = null,
          [Description("Optional: Include movies these people ACTED in, as comma-separated PersonIds. " +
                       "Actors only - a director will not match here.")] string? castIds = null,
          [Description("Optional: Include movies these people worked on BEHIND the camera, as " +
                       "comma-separated PersonIds - directors, writers, producers, composers. " +
                       "Use this for 'directed by', 'written by' or 'films by'. A director does not " +
                       "appear in castIds, so filtering a director as cast returns nothing.")] string? crewIds = null,
          [Description("Optional: Include movies with these genre IDs (comma-separated)")] string? genreIds = null,
          [Description("Optional: Include movies with these keyword IDs (comma-separated)")] string? keywordIds = null,
          [Description("Optional: Minimum vote average (1-10)")] double? minVoteAverage = null,
          [Description("Optional: Maximum vote average (1-10)")] double? maxVoteAverage = null,
          [Description("Optional: Minimum vote count")] int? minVoteCount = null,
          [Description("Optional: Maximum vote count")] int? maxVoteCount = null,
          [Description("Optional: How to order the results. One of 'popularity', 'rating', 'newest', " +
                       "'oldest', 'revenue', 'votes'. Use 'rating' for 'best' or 'top rated' - it applies " +
                       "a minimum vote count so obscure films with one perfect vote do not win. Note this " +
                       "orders by the TMDb vote average, which is not the IMDb rating: to answer a rating " +
                       "question, still call GetMovieRating or CompareMovieRatings on the results.")] string? sortBy = null,
          [Description("Optional: 'all' (default) requires a movie to match every genre id supplied; " +
                       "'any' matches at least one. Use 'any' for 'action or comedy'.")] string? genreMatch = null
      )
        {
            DiscoverMovie query = client.DiscoverMoviesAsync();

            var sort = ParseSortBy(sortBy);
            if (sort.HasValue)
            {
                query = query.OrderBy(sort.Value);

                if (sort.Value == DiscoverMovieSortBy.VoteAverageDesc && !minVoteCount.HasValue)
                {
                    minVoteCount = RatingSortMinimumVotes;
                }
            }

            // Apply release date filters
            if (!string.IsNullOrEmpty(releaseDateFrom))
            {
                var releaseDate = DateTime.Parse(releaseDateFrom);
                query = query.WherePrimaryReleaseDateIsAfter(releaseDate);
            }

            if (!string.IsNullOrEmpty(releaseDateTo))
            {
                var releaseDate = DateTime.Parse(releaseDateTo);
                query = query.WherePrimaryReleaseDateIsBefore(releaseDate);
            }

            // Apply cast filters
            if (!string.IsNullOrEmpty(castIds))
            {
                var castIdList = ParseIdList(castIds);
                if (castIdList.Count > 0)
                {
                    query = query.IncludeWithAllOfCast(castIdList);
                }
            }

            // Apply crew filters. TMDb keeps directing, writing and producing credits in a separate
            // bucket from acting credits, and only with_cast was ever wired up - so "sci-fi directed by
            // Christopher Nolan" resolved him to PersonId 525, filtered it as *cast*, and got zero rows
            // back (with_cast=525 alone returns documentaries about him, not films by him). An empty
            // result is exactly when the model falls back on memory and starts inventing titles, which
            // is what the system prompt's whole "call the functions" section exists to prevent.
            // with_crew=525 returns Interstellar, Inception, The Prestige and Tenet.
            if (!string.IsNullOrEmpty(crewIds))
            {
                var crewIdList = ParseIdList(crewIds);
                if (crewIdList.Count > 0)
                {
                    query = query.IncludeWithAllOfCrew(crewIdList);
                }
            }

            // Apply genre filters. "all" stays the default because "action comedy" genuinely means
            // both; "any" exists because with only the AND form, "action or comedy movies" asked TMDb
            // for films that are simultaneously action and comedy and came back with almost nothing.
            if (!string.IsNullOrEmpty(genreIds))
            {
                var genreIdList = ParseIdList(genreIds);
                if (genreIdList.Count > 0)
                {
                    query = string.Equals(genreMatch?.Trim(), "any", StringComparison.OrdinalIgnoreCase)
                        ? query.IncludeWithAnyOfGenre(genreIdList)
                        : query.IncludeWithAllOfGenre(genreIdList);
                }
            }

            // Apply keyword filters
            if (!string.IsNullOrEmpty(keywordIds))
            {
                var keywordIdList = ParseIdList(keywordIds);
                if (keywordIdList.Count > 0)
                {
                    query = query.IncludeWithAllOfKeywords(keywordIdList);
                }
            }

            // Apply vote average filters
            if (minVoteAverage.HasValue)
            {
                query = query.WhereVoteAverageIsAtLeast(minVoteAverage.Value);
            }

            if (maxVoteAverage.HasValue)
            {
                query = query.WhereVoteAverageIsAtMost(maxVoteAverage.Value);
            }

            // Apply vote count filters
            if (minVoteCount.HasValue)
            {
                query = query.WhereVoteCountIsAtLeast(minVoteCount.Value);
            }

            if (maxVoteCount.HasValue)
            {
                query = query.WhereVoteCountIsAtMost(maxVoteCount.Value);
            }

            // Execute query and get results with IMDb IDs
            var searchResults = await query.Query();

            // A bare "[]" tells the model nothing about why, and an empty lookup is precisely the moment
            // it stops calling functions and starts answering from memory. Filtering a director as cast
            // is the way this goes wrong in practice, so that case gets told what to do instead.
            if (searchResults.Results.Count == 0
                && !string.IsNullOrEmpty(castIds)
                && string.IsNullOrEmpty(crewIds))
            {
                return JsonSerializer.Serialize(new
                {
                    Results = Array.Empty<MovieSearchResult>(),
                    Hint = "No movies matched those cast ids. If the person is a director, writer or "
                         + "producer rather than an actor, pass their PersonId as crewIds instead of "
                         + "castIds and try again. Do not answer from memory."
                });
            }

            var movieList = await ToSearchResultsAsync(
                searchResults.Results,
                movie => movie.Id,
                movie => movie.Title,
                movie => movie.ReleaseDate,
                async id => (await client.GetMovieExternalIdsAsync(id)).ImdbId);

            return JsonSerializer.Serialize(movieList);
        }
    }
}