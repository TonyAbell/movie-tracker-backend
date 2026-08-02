using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using TMDbLib.Client;
using TMDbLib.Objects.Discover;

namespace MovieTracker.Backend.Prompts
{
    public record MovieSearchResult(
        string MovieId,
        string MovieName,
        string ReleaseDate,
        string ImdbId,
        int? RuntimeMinutes = null);
    public record GenresItem(string GenreId, string GenreName);

    public class TheMovieDBKernelFunctions(TMDbClient client)
    {
        /// <summary>
        /// How many TMDb calls the id fan-out below is allowed to have in flight at once. TMDb rate
        /// limits, and a search can return twenty results, so this is deliberately not unbounded.
        /// </summary>
        private const int MaxConcurrentTmdbCalls = 8;

        /// <summary>
        /// Cap on credits returned per person. A prolific actor has hundreds, and every one of them is
        /// context the model pays for on this turn and every turn after it until compaction collapses
        /// the call. The payload says how many were withheld rather than truncating silently.
        /// </summary>
        private const int MaxPersonCredits = 25;

        /// <summary>
        /// How many credits to look up genres for when a genre filter is supplied. The filter has to run
        /// before the cap - filtering the top 25 by popularity would miss a qualifying film that sits at
        /// 30 - so the candidate pool is widened first. Only paid when genreIds is actually passed, and
        /// still bounded: a director's whole filmography fits comfortably inside this.
        /// </summary>
        private const int MaxGenreFilterLookups = 60;

        /// <summary>
        /// TMDb keys content ratings by country and there is no global one. US is hardcoded to match the
        /// rest of the app, which already assumes an English-language, US-centric catalogue.
        /// </summary>
        private const string CertificationCountry = "US";

        /// <summary>
        /// Resolves TMDb movie ids to IMDb ids, concurrently, bounded by
        /// <see cref="MaxConcurrentTmdbCalls"/>.
        /// <para>
        /// TMDb has no batch endpoint for external ids, so this is one call per movie either way - but
        /// it used to be one call per movie <i>sequentially</i>, inside a foreach. That put up to twenty
        /// round trips on the critical path of a single tool call: measured in production at 0.91s mean
        /// for SearchMovies and 0.99s for DiscoverMovies, which is what ~21 serial ~40ms calls costs.
        /// Benchmarked on 20-result searches, concurrent is 2182ms -> 208ms, 2375 -> 148, 2218 -> 106.
        /// </para>
        /// <para>
        /// An id that cannot be resolved maps to "" rather than failing the batch: the model can still
        /// use the MovieId, and one missing IMDb id should not lose the other nineteen results.
        /// </para>
        /// </summary>
        private async Task<Dictionary<int, string>> ResolveImdbIdsAsync(IEnumerable<int> movieIds)
        {
            var resolved = new ConcurrentDictionary<int, string>();
            using var throttle = new SemaphoreSlim(MaxConcurrentTmdbCalls);

            await Task.WhenAll(movieIds.Distinct().Select(async movieId =>
            {
                await throttle.WaitAsync();
                try
                {
                    resolved[movieId] = (await client.GetMovieExternalIdsAsync(movieId)).ImdbId ?? "";
                }
                catch
                {
                    resolved[movieId] = "";
                }
                finally
                {
                    throttle.Release();
                }
            }));

            return new Dictionary<int, string>(resolved);
        }

        /// <summary>What a per-movie detail lookup contributes beyond the credit itself.</summary>
        private sealed record MovieFacts(
            string ImdbId,
            IReadOnlyList<int> GenreIds,
            IReadOnlyList<string> GenreNames,
            int? Runtime);

        /// <summary>
        /// Like <see cref="ResolveImdbIdsAsync"/> but fetches the whole movie, because TMDb's person
        /// credits carry no genre at all on crew entries - <c>MovieJob</c> has Title, Job and dates and
        /// nothing else, so "sci-fi films X directed" cannot be answered from the credits list alone.
        /// <para>
        /// This is the same <i>number</i> of round trips as resolving external ids: one per movie either
        /// way, bounded by <see cref="MaxConcurrentTmdbCalls"/>. GetMovieAsync just returns more from
        /// each of them, including the ImdbId that lookup existed for. The lighter external-ids path is
        /// kept for search and discover, which need no genres.
        /// </para>
        /// </summary>
        private async Task<Dictionary<int, MovieFacts>> ResolveMovieFactsAsync(IEnumerable<int> movieIds)
        {
            var resolved = new ConcurrentDictionary<int, MovieFacts>();
            using var throttle = new SemaphoreSlim(MaxConcurrentTmdbCalls);

            await Task.WhenAll(movieIds.Distinct().Select(async movieId =>
            {
                await throttle.WaitAsync();
                try
                {
                    var movie = await client.GetMovieAsync(movieId);
                    resolved[movieId] = new MovieFacts(
                        movie.ImdbId ?? "",
                        [.. movie.Genres?.Select(genre => genre.Id) ?? []],
                        [.. movie.Genres?.Select(genre => genre.Name).Where(name => name is not null) ?? []],
                        movie.Runtime);
                }
                catch
                {
                    // A movie we cannot describe still belongs in the list under its own title; it just
                    // cannot participate in genre filtering.
                    resolved[movieId] = new MovieFacts("", [], [], null);
                }
                finally
                {
                    throttle.Release();
                }
            }));

            return new Dictionary<int, MovieFacts>(resolved);
        }

        /// <summary>
        /// Projects search hits into MovieSearchResults, resolving IMDb ids concurrently. Written by
        /// index rather than appended, so the order TMDb ranked them in survives.
        /// </summary>
        private async Task<List<MovieSearchResult>> ToSearchResultsAsync<T>(
            IReadOnlyList<T> movies,
            Func<T, int> getId,
            Func<T, string?> getTitle,
            Func<T, DateTime?> getReleaseDate)
        {
            var imdbIds = await ResolveImdbIdsAsync(movies.Select(getId));

            return [.. movies.Select(movie => new MovieSearchResult(
                getId(movie).ToString(),
                getTitle(movie) ?? "",
                getReleaseDate(movie)?.ToString("yyyy-MM-dd") ?? "",
                imdbIds.TryGetValue(getId(movie), out var imdbId) ? imdbId : ""))];
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
            AIFunctionFactory.Create(GetPersonMovieCredits),
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
        private static bool TryGetTmdbId(string? movieId, out int id, out string error) =>
            TryGetId(movieId, "movie", "13", "Call SearchMovies or DiscoverMovies first and use the MovieId it returns.", out id, out error);

        private static bool TryGetPersonId(string? personId, out int id, out string error) =>
            TryGetId(personId, "person", "525", "Call SearchForPeople first and use the PersonId it returns.", out id, out error);

        private static bool TryGetId(string? value, string kind, string example, string hint, out int id, out string error)
        {
            if (int.TryParse(value, out id))
            {
                error = string.Empty;
                return true;
            }

            error = JsonSerializer.Serialize(new
            {
                Error = $"'{value}' is not a valid TMDb {kind} id. Ids are numeric, e.g. '{example}'.",
                Hint = hint
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

        /// <summary>
        /// A person's filmography, with the job they actually did on each film.
        /// <para>
        /// This exists because TMDb's discover endpoint can filter by crew <i>membership</i> but not by
        /// crew <i>job</i>. `DiscoverMovies(crewIds:)` therefore answers "worked on" rather than
        /// "directed", and measured against the live API that is a wide gap: Tarantino's 1990s crew
        /// results include True Romance (Writer), Natural Born Killers (Story), Killing Zoe (Executive
        /// Producer) and Jackie Chan: My Story, where his only credit is <c>Thanks</c>. One call here
        /// returns every credit already carrying its Job, so it is both more accurate than
        /// discover-then-filter and cheaper than it.
        /// </para>
        /// </summary>
        [Description("Get one person's filmography from their PersonId, with the exact job they did on " +
                     "each film. This is the right tool for 'what did X direct', 'what did X write' and " +
                     "'what has X been in', because it is the only one that can tell a directing credit " +
                     "from a writing or producing one - pass job='Director' for 'directed by'. Prefer " +
                     "this over DiscoverMovies(crewIds:) for any question about one person's own work. " +
                     "For 'sci-fi films X directed', pass genreIds as well and let this filter them; the " +
                     "returned credits also carry Genres so you can narrow further yourself.")]
        [return: Description("JSON with the person's credits: MovieId, MovieName, ReleaseDate, ImdbId, Genres, and either Character (acting) or Job")]
        public async Task<string> GetPersonMovieCredits(
            [Description("The TMDb PersonId, as returned by SearchForPeople")] string personId,
            [Description("Optional: keep only credits of this kind. 'Director' for 'directed by', " +
                         "'Writer' for 'wrote', 'Producer' for 'produced', 'Actor' for 'starred in'. " +
                         "Omit for acting roles plus all crew work.")] string? job = null,
            [Description("Optional: only films released in or after this year (YYYY)")] string? fromYear = null,
            [Description("Optional: only films released in or before this year (YYYY)")] string? toYear = null,
            [Description("Optional: keep only films in these genres, as comma-separated genre ids from " +
                         "GetGenresList. Use this for 'sci-fi films X directed' rather than filtering the " +
                         "results yourself - a person's credits do not otherwise carry genres.")] string? genreIds = null,
            [Description("Optional: how to order the credits. 'popularity' (default, best known first), " +
                         "'longest' or 'shortest' by runtime, 'newest' or 'oldest' by release date. For " +
                         "'the longest film X directed' pass 'longest' and take the first result - do not " +
                         "ask for the default order and compare the runtimes yourself.")] string? sortBy = null)
        {
            if (!TryGetPersonId(personId, out var tmdbPersonId, out var idError)) return idError;

            var credits = await client.GetPersonMovieCreditsAsync(tmdbPersonId);

            var wantJob = job?.Trim();
            var filterByJob = !string.IsNullOrEmpty(wantJob);

            var (jobKind, jobNames) = filterByJob
                ? ResolveJobFilter(wantJob!)
                : (CreditKind.Crew, []);

            // Acting credits are excluded when a *crew* job was asked for - "what did Nolan direct"
            // should not come back with his cameos - but they are the whole answer when the job asked
            // for is an acting one.
            var includeCast = !filterByJob || jobKind == CreditKind.Cast;
            var includeCrew = !filterByJob || jobKind == CreditKind.Crew;

            var acting = !includeCast
                ? []
                : (credits.Cast ?? []).Select(role => new
                {
                    MovieId = role.Id.ToString(),
                    MovieName = role.Title,
                    role.ReleaseDate,
                    Credit = string.IsNullOrWhiteSpace(role.Character) ? "Actor" : $"as {role.Character}",
                    // Only cast credits carry this; MovieJob has no popularity at all.
                    Popularity = (double?)role.Popularity,
                }).ToList();

            var working = (!includeCrew ? [] : (credits.Crew ?? []))
                .Where(crew => !filterByJob
                    || jobNames.Contains(crew.Job ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .Select(crew => new
                {
                    MovieId = crew.Id.ToString(),
                    MovieName = crew.Title,
                    crew.ReleaseDate,
                    Credit = crew.Job,
                    Popularity = (double?)null,
                })
                .ToList();

            var all = acting.Concat(working)
                .Where(credit => WithinYears(credit.ReleaseDate, fromYear, toYear))
                // The same film can appear once per job (Tarantino is Writer *and* Director on Pulp
                // Fiction); collapse those so the model sees one entry per movie.
                .GroupBy(credit => credit.MovieId)
                .Select(group => new
                {
                    group.First().MovieId,
                    group.First().MovieName,
                    ReleaseDate = group.First().ReleaseDate?.ToString("yyyy-MM-dd") ?? "",
                    Credit = string.Join(", ", group.Select(c => c.Credit).Distinct()),
                    Popularity = group.Max(c => c.Popularity),
                    Date = group.First().ReleaseDate ?? DateTime.MinValue,
                })
                // Popularity first where it exists, so "what has Meg Ryan been in?" leads with When Harry
                // Met Sally rather than whatever she filmed most recently - sorting acting credits by date
                // buries the famous work under the obscure. Crew credits have no popularity, so a
                // job-filtered query falls through to newest-first, which is what you want for those.
                .OrderByDescending(credit => credit.Popularity ?? -1)
                .ThenByDescending(credit => credit.Date)
                .ToList();

            var wantGenres = string.IsNullOrEmpty(genreIds) ? [] : ParseIdList(genreIds);
            var filterByGenre = wantGenres.Count > 0;

            var order = sortBy?.Trim().ToLowerInvariant();
            // Runtime is only known after the detail lookup below, so a runtime sort has to see more
            // than the final 25 - same reason genre filtering does.
            var sortNeedsRuntime = order is "longest" or "shortest";

            // Genre filtering and runtime sorting both have to happen before the cap, so widen the
            // candidate pool when either is asked for - filtering or sorting the top 25 by popularity
            // would silently miss a qualifying film sitting at position 30.
            var candidates = all.Take(filterByGenre || sortNeedsRuntime ? MaxGenreFilterLookups : MaxPersonCredits).ToList();

            // Resolve details here rather than telling the model to go and fetch them.
            //
            // The first version of this method omitted ImdbId and pointed the model at DescribeMovie,
            // reasoning that skipping the fan-out was cheaper. Measured on the deployed app, it was the
            // opposite: the model dutifully called DescribeMovie once per film, and tool calls per turn
            // went from a max of 3 to a max of 10 against a cap of 12. The N+1 had not gone away, it had
            // moved from HTTP round trips into *model* iterations - roughly 1s each instead of 40ms, and
            // charged against the iteration budget, where running out truncates the answer.
            var facts = await ResolveMovieFactsAsync(
                candidates.Select(credit => int.TryParse(credit.MovieId, out var id) ? id : 0).Where(id => id > 0));

            static MovieFacts FactsFor(Dictionary<int, MovieFacts> lookup, string movieId) =>
                int.TryParse(movieId, out var id) && lookup.TryGetValue(id, out var found)
                    ? found
                    : new MovieFacts("", [], [], null);

            var matching = filterByGenre
                ? candidates.Where(credit => FactsFor(facts, credit.MovieId).GenreIds.Intersect(wantGenres).Any()).ToList()
                : candidates;

            // Sorting here rather than leaving the model to compare. Asked for Nolan's longest film with
            // the runtimes sitting in front of it, gpt-5.4-mini repeatedly answered "The Odyssey at 173
            // minutes, which edges out Oppenheimer by 8 minutes" - it computed the gap correctly and
            // attributed it backwards, anchoring on whichever title came first. Ordering in code makes
            // the answer the first element instead of an arithmetic exercise.
            matching = order switch
            {
                "longest" => [.. matching.OrderByDescending(c => FactsFor(facts, c.MovieId).Runtime ?? 0)],
                "shortest" => [.. matching
                    .Where(c => FactsFor(facts, c.MovieId).Runtime is > 0)
                    .OrderBy(c => FactsFor(facts, c.MovieId).Runtime)],
                "newest" => [.. matching.OrderByDescending(c => c.Date)],
                "oldest" => [.. matching.Where(c => c.Date > DateTime.MinValue).OrderBy(c => c.Date)],
                _ => matching,
            };

            var shown = matching.Take(MaxPersonCredits).Select(credit => new
            {
                credit.MovieId,
                credit.MovieName,
                credit.ReleaseDate,
                credit.Credit,
                FactsFor(facts, credit.MovieId).ImdbId,
                // Carried even when no filter was applied: it lets the model narrow the list itself for
                // anything the genre ids cannot express ("his war films", "something funny").
                Genres = FactsFor(facts, credit.MovieId).GenreNames,
                // Free - the detail lookup above already returned it. Without it "the longest film X
                // directed" cannot be answered from this payload, and the model guesses a likely title
                // and verifies only that one: asked for Nolan's longest it answered The Odyssey (173
                // min) having never looked at Oppenheimer (181).
                RuntimeMinutes = FactsFor(facts, credit.MovieId).Runtime,
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                PersonId = personId,
                JobFilter = filterByJob ? wantJob : "(none - acting and all crew credits)",
                GenreFilter = filterByGenre ? genreIds : "(none)",
                SortedBy = order is "longest" or "shortest" or "newest" or "oldest" ? order : "popularity",
                TotalCredits = all.Count,
                TotalMatching = matching.Count,
                Returned = shown.Count,
                Credits = shown,
                // Say so rather than silently truncating, so the model does not present a capped list
                // as a complete filmography.
                Note = matching.Count > shown.Count
                    ? $"Showing {shown.Count} of {matching.Count} matching credits, best known first."
                    : "Complete list for this filter.",
                Hint = "ImdbId is included - pass it straight to GetMovieRating or CompareMovieRatings. "
                     + "No need to call DescribeMovie first."
            });
        }

        /// <summary>Whether a job word refers to on-screen work or behind-the-camera work.</summary>
        private enum CreditKind { Cast, Crew }

        /// <summary>
        /// Maps the job word the model supplies onto how TMDb actually credits it.
        /// <para>
        /// Two failures made this necessary, both caught by logging the arguments the model really
        /// passed. First, <c>job=Actor</c>: acting is not a crew job in TMDb, so a naive crew-only
        /// filter dropped the cast list *and* matched nothing, returning zero credits for "the worst
        /// rated Adam Sandler movie" - a question about an actor's films. Second, <c>job=Writer</c>:
        /// TMDb spreads writing across Writer, Screenplay and Story, so an exact match silently loses
        /// whichever spelling a given film happens to use. Both are the obvious thing for a model to
        /// pass, so both have to work.
        /// </para>
        /// <para>
        /// An unrecognised value falls through as an exact crew-job match, which keeps the tool usable
        /// for the long tail ("Costume Design") without needing a table of every TMDb job.
        /// </para>
        /// </summary>
        private static (CreditKind Kind, string[] Jobs) ResolveJobFilter(string job) =>
            job.Trim().ToLowerInvariant() switch
            {
                "actor" or "actress" or "acting" or "cast" or "star" or "starring" or "self"
                    => (CreditKind.Cast, []),
                "director" or "directed" or "directing" or "filmmaker"
                    => (CreditKind.Crew, ["Director"]),
                "writer" or "writing" or "written" or "screenwriter" or "screenplay" or "author"
                    => (CreditKind.Crew, ["Writer", "Screenplay", "Story", "Author"]),
                "producer" or "produced" or "producing"
                    => (CreditKind.Crew, ["Producer", "Executive Producer", "Co-Producer", "Associate Producer"]),
                "composer" or "music"
                    => (CreditKind.Crew, ["Original Music Composer", "Composer", "Music"]),
                "cinematographer" or "cinematography"
                    => (CreditKind.Crew, ["Director of Photography", "Cinematography"]),
                "editor" or "editing"
                    => (CreditKind.Crew, ["Editor", "Film Editor"]),
                _ => (CreditKind.Crew, [job.Trim()]),
            };

        /// <summary>Year-bounds filter that treats an unparseable or absent year as "no bound".</summary>
        private static bool WithinYears(DateTime? releaseDate, string? fromYear, string? toYear)
        {
            if (releaseDate is not { } date) return string.IsNullOrEmpty(fromYear) && string.IsNullOrEmpty(toYear);

            if (int.TryParse(fromYear, out var from) && date.Year < from) return false;
            if (int.TryParse(toYear, out var to) && date.Year > to) return false;

            return true;
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
                movie => movie.ReleaseDate);

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
                     "(with ids), runtime, tagline, language, top-billed cast, poster art, worldwide box " +
                     "office and budget, and the ImdbId. This is the single movie-detail tool - use it " +
                     "whenever you need more about a film than a search result carries, including 'how " +
                     "much did it make' and 'who was in it'. For the rating, call GetMovieRating with the " +
                     "ImdbId this returns.")]
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
                // Worldwide, in USD, straight off the movie record this call already fetched. Asked what
                // Titanic made, the model previously had nowhere to get this and answered from memory
                // via a Wikipedia context call. OMDb's BoxOffice is US-domestic only, so it is not a
                // substitute for the figure people mean.
                WorldwideRevenueUsd = movie.Revenue,
                BudgetUsd = movie.Budget,
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
          [Description("Optional: Include movies these people worked on BEHIND the camera in ANY role - " +
                       "director, writer, producer, composer, editor - as comma-separated PersonIds. " +
                       "This cannot distinguish those roles: it matches any crew credit, including " +
                       "'Thanks'. For 'directed by' or 'written by' specifically, use " +
                       "GetPersonMovieCredits with a job filter instead. Use this one only to combine a " +
                       "behind-the-camera person with other filters like genre or date.")] string? crewIds = null,
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
                       "'any' matches at least one. Use 'any' for 'action or comedy'.")] string? genreMatch = null,
          [Description("Optional: US content rating ceiling - 'G', 'PG', 'PG-13', 'R' or 'NC-17'. " +
                       "Returns only films rated at or below it. Use this for anything about kids, " +
                       "families or age-appropriateness; do not judge a film's rating yourself.")] string? maxCertification = null,
          [Description("Optional: exact US content rating, e.g. 'PG-13'. Prefer maxCertification unless " +
                       "the user asked for precisely one rating.")] string? certification = null,
          [Description("Optional: longest runtime in minutes. Use this for 'under two hours' or " +
                       "'something short' instead of fetching each film to check.")] int? maxRuntimeMinutes = null,
          [Description("Optional: shortest runtime in minutes, for 'a proper long epic'.")] int? minRuntimeMinutes = null
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

            // Content rating. Without this the model had no way to honour "PG-13" or "for the kids", so
            // it approximated with genre ids and its own sense of what is family-safe - and got it
            // wrong: asked for PG-13 family movie night it returned The Matrix, which is rated R, and
            // said it was not "full hard-R". TMDb keys certifications by country and only US is wired
            // up here, which is the same assumption the rest of the app already makes.
            if (!string.IsNullOrWhiteSpace(maxCertification))
            {
                query = query.WhereCertificationIsAtMost(CertificationCountry, maxCertification.Trim().ToUpperInvariant());
            }

            if (!string.IsNullOrWhiteSpace(certification))
            {
                query = query.WhereCertificationIs(CertificationCountry, certification.Trim().ToUpperInvariant());
            }

            // Runtime. Without these, "a 70s horror film under two hours" ran an unfiltered discover and
            // then spent five DescribeMovie calls checking runtimes one at a time - the same N+1 through
            // the model that GetPersonMovieCredits used to cause, and it still let a 122-minute film
            // through. TMDb can answer this in the same request.
            if (maxRuntimeMinutes is > 0)
            {
                query = query.WhereRuntimeIsAtMost(maxRuntimeMinutes.Value);
            }

            if (minRuntimeMinutes is > 0)
            {
                query = query.WhereRuntimeIsAtLeast(minRuntimeMinutes.Value);
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
            var wantsRuntimeFilter = maxRuntimeMinutes is > 0 || minRuntimeMinutes is > 0;

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

            // TMDb's with_runtime cannot be trusted, so it is used to narrow the set and then verified
            // here. Measured against the live API: with_runtime.gte=181 returned Spider-Man (121 min),
            // Interstellar (169) and Fellowship of the Ring (179) - six of the first ten results
            // contradicted TMDb's *own* runtime field, because the filter matches alternate release cuts
            // rather than the canonical runtime. Sending that back unchecked is how "under two hours"
            // came back with a 124-minute film.
            if (wantsRuntimeFilter)
            {
                var facts = await ResolveMovieFactsAsync(searchResults.Results.Select(movie => movie.Id));

                var verified = searchResults.Results
                    .Select(movie => (Movie: movie, Runtime: facts.TryGetValue(movie.Id, out var f) ? f.Runtime : null))
                    // An unknown runtime cannot be shown to satisfy the constraint, so it is dropped
                    // rather than assumed to pass.
                    .Where(entry => entry.Runtime is > 0
                        && (maxRuntimeMinutes is not > 0 || entry.Runtime <= maxRuntimeMinutes)
                        && (minRuntimeMinutes is not > 0 || entry.Runtime >= minRuntimeMinutes))
                    .Select(entry => new MovieSearchResult(
                        entry.Movie.Id.ToString(),
                        entry.Movie.Title ?? "",
                        entry.Movie.ReleaseDate?.ToString("yyyy-MM-dd") ?? "",
                        facts.TryGetValue(entry.Movie.Id, out var found) ? found.ImdbId : "",
                        entry.Runtime))
                    .ToList();

                return JsonSerializer.Serialize(new
                {
                    Results = verified,
                    Note = $"{verified.Count} of {searchResults.Results.Count} results actually satisfy the "
                         + "runtime filter; TMDb's own runtime filter is unreliable, so the rest were "
                         + "verified and dropped here. RuntimeMinutes is the checked value.",
                });
            }

            var movieList = await ToSearchResultsAsync(
                searchResults.Results,
                movie => movie.Id,
                movie => movie.Title,
                movie => movie.ReleaseDate);

            return JsonSerializer.Serialize(movieList);
        }
    }
}