using Microsoft.Extensions.AI;
using MovieTracker.Backend.Agents;
using System.ComponentModel;
using System.Text.Json;

namespace MovieTracker.Backend.Prompts
{
    /// <summary>
    /// The facade the model sees over the Agents/ services - ratings, trailers and the
    /// Wikipedia-backed funny facts and context. Under Semantic Kernel this was turned into a plugin
    /// per request with KernelPluginFactory.CreateFromObject; the Agent Framework equivalent is the
    /// explicit CreateTools() list below, which Program.cs folds into the agent's tool set.
    /// </summary>
    public class ChatPlanner
    {
        // The bare IChatClient, not the AIAgent: these are single-shot prompts that must not inherit
        // the agent's tool set or its JSON response format. See the note on TrailerAgent.chatClient.
        private readonly IChatClient chatClient;
        private readonly WikipediaSearchAgent wikipediaAgent;
        private readonly OpenMovieDbAgent openMovieDbAgent;
        private readonly TrailerAgent trailerAgent;

        public ChatPlanner(IChatClient chatClient, WikipediaSearchAgent wikipediaAgent, OpenMovieDbAgent openMovieDbAgent, TrailerAgent trailerAgent)
        {
            this.chatClient = chatClient;
            this.wikipediaAgent = wikipediaAgent;
            this.openMovieDbAgent = openMovieDbAgent;
            this.trailerAgent = trailerAgent;
        }

        /// <summary>
        /// Explicit tool list; see the note on TheMovieDBKernelFunctions.CreateTools.
        /// </summary>
        /// <remarks>
        /// <see cref="GenerateEnhancedFunnyFact"/> is the one method here that is still called but not
        /// offered: Chat-Ask runs it out of band for every request, racing the main model call. As a
        /// tool it bought nothing and cost a nested completion plus Wikipedia and Wikidata round trips
        /// inside a call the model was already waiting on, and the fact reaches the client through the
        /// response's own FunnyFact field rather than through the conversation.
        ///
        /// Three further methods were withdrawn at the migration and have since been deleted outright,
        /// since nothing called them: GenerateFunnyFact (a stale copy of DetectEntity plus the
        /// ungrounded fallback), GenerateRequiredSteps (a fixed string restating a JSON shape the system
        /// prompt states and the strict schema enforces - and whose example contained a fabricated
        /// MovieId and an ImdbId field the schema rejects), and GetMovieRatingGeneric (the same IMDb
        /// lookup as <see cref="GetMovieRating"/> under a second description, which is just an ambiguous
        /// choice for the model to get wrong).
        ///
        /// The date helpers in <see cref="DateTimeKernelFunctions"/> were left alone on purpose: their
        /// schemas are tiny, and they exist precisely to stop the model inventing date ranges.
        /// </remarks>
        public IEnumerable<AITool> CreateTools() =>
        [
            AIFunctionFactory.Create(HandleTrailerRequest),
            AIFunctionFactory.Create(GetMovieRating),
            AIFunctionFactory.Create(CompareMovieRatings),
            AIFunctionFactory.Create(FilterMoviesByRating),
            AIFunctionFactory.Create(GetChatContext),
            AIFunctionFactory.Create(GetMovieContext),
        ];

        [Description("Handle a request for a movie trailer and return a clickable trailer link. " +
                     "Only call this when the user actually asked for a trailer, teaser, preview or promo.")]
        public async Task<string> HandleTrailerRequest(
            [Description("The user's request, verbatim")] string userQuery)
        {
            if (trailerAgent.CanHandle(userQuery))
            {
                return await trailerAgent.HandleRequest(userQuery);
            }

            // Used to return GenerateRequiredSteps(), a restatement of the response schema whose worked
            // example contained "MovieId": "1" - a fabricated id of exactly the kind the system prompt
            // forbids - plus an ImdbId field that DisallowAdditionalProperties rejects. Handing that to
            // the model mid-conversation taught it the wrong shape. Same {Error, Hint} envelope the TMDb
            // tools use for a correctable mistake.
            return JsonSerializer.Serialize(new
            {
                Error = "That does not look like a trailer request, so no trailer was looked up.",
                Hint = "Answer the question with the movie search tools instead. Only call " +
                       "HandleTrailerRequest when the user asks for a trailer, teaser, preview or promo."
            });
        }

        // Absorbs the description GetMovieRatingGeneric used to carry, since that near-duplicate tool is
        // no longer offered: the "user said rating without naming a source" case is the whole reason it
        // existed, and IMDb-as-default is a deliberate choice for this app rather than an accident.
        // The last sentence is load-bearing - without it the model will happily answer a rating question
        // from a TMDb vote average already sitting in context instead of looking the rating up.
        [Description("Get a movie's rating from its IMDb ID. Use this whenever the user asks about a rating, " +
                     "score or 'which was highest rated' without naming a source: IMDb is the default rating. " +
                     "Always returns the IMDb rating as the primary rating, with Rotten Tomatoes and Metacritic " +
                     "as secondary context only. Prefer calling this over quoting any TMDb vote average that " +
                     "appeared in earlier search or detail results.")]
        [return: Description("JSON object containing IMDb rating as the primary rating, with other ratings as additional context")]
        public async Task<string> GetMovieRating(
            [Description("The IMDb ID of the movie (e.g., 'tt1375666')")] string imdbId)
        {
            var result = await openMovieDbAgent.GetMovieRatings(imdbId);

            if (!result.IsSuccess)
            {
                return JsonSerializer.Serialize(new { Error = result.ErrorMessage });
            }

            return JsonSerializer.Serialize(new
            {
                Title = result.Title,
                Year = result.Year,
                ImdbRating = result.ImdbRating,
                RottenTomatoesRating = result.RottenTomatoesRating,
                MetacriticRating = result.MetacriticRating,
                BoxOffice = result.BoxOffice,
                Summary = $"{result.Title} ({result.Year}) has an IMDb rating of {result.ImdbRating}"
            });
        }

        [Description("Compare IMDb ratings of multiple movies and find the highest rated one. Always uses IMDb ratings for comparison.")]
        [return: Description("Comparison results showing which movie has the highest IMDb rating")]
        public async Task<string> CompareMovieRatings(
            [Description("Comma-separated list of IMDb IDs to compare (e.g., 'tt0068646,tt0071562,tt0099685')")] string imdbIds)
        {
            var ids = imdbIds.Split(',').Select(id => id.Trim()).ToList();
            var result = await openMovieDbAgent.CompareMovieRatings(ids);

            if (!result.IsSuccess)
            {
                return JsonSerializer.Serialize(new { Error = result.ErrorMessage });
            }

            return JsonSerializer.Serialize(new
            {
                Winner = result.HighestRatedTitle,
                HighestRating = result.HighestRating,
                AllMovies = result.AllMovies.Select(m => new
                {
                    Title = $"{m.Title} ({m.Year})",
                    ImdbRating = m.ImdbRating,
                    RottenTomatoesRating = m.RottenTomatoesRating,
                    MetacriticRating = m.MetacriticRating
                }),
                Summary = $"{result.HighestRatedTitle} has the highest IMDb rating of {result.HighestRating}"
            });
        }

        [Description("Filter movies by IMDb rating threshold. Always uses IMDb ratings for filtering.")]
        [return: Description("List of movies that meet the minimum IMDb rating requirement")]
        public async Task<string> FilterMoviesByRating(
            [Description("Comma-separated list of IMDb IDs to filter")] string imdbIds,
            [Description("Minimum IMDb rating threshold (e.g., 7.0)")] double minimumRating)
        {
            var ids = imdbIds.Split(',').Select(id => id.Trim()).ToList();
            var qualifyingMovies = await openMovieDbAgent.FilterMoviesByRating(ids, minimumRating);

            return JsonSerializer.Serialize(new
            {
                MinimumRating = minimumRating,
                TotalMoviesChecked = ids.Count,
                QualifyingMoviesCount = qualifyingMovies.Count,
                QualifyingMovies = qualifyingMovies.Select(m => new
                {
                    Title = $"{m.Title} ({m.Year})",
                    ImdbRating = m.ImdbRating,
                    RottenTomatoesRating = m.RottenTomatoesRating,
                    MetacriticRating = m.MetacriticRating,
                    BoxOffice = m.BoxOffice
                }),
                Summary = $"Found {qualifyingMovies.Count} out of {ids.Count} movies with IMDb rating {minimumRating}+"
            });
        }

        [Description("Enhanced funny fact generator using Wikipedia data")]
        public async Task<string?> GenerateEnhancedFunnyFact(string userQuery)
        {
            var entity = await DetectEntity(userQuery);

            if (entity.IsNone) return null;

            var wikipediaInfo = await wikipediaAgent.GetEnhancedInfo(entity.Name, entity.Type);

            // Below this bar there is nothing to ground a fact in. This used to fall back to a prompt
            // that asked the model to invent one from memory, and the result went straight to the user
            // as FunnyFact - confident, unsourced biographical claims about real people. It fired on
            // every person query, because the entity type was hardcoded to "movie" and the film SPARQL
            // never matches a human, so confidence never cleared this gate. No fact beats a made-up one:
            // FunnyFact is optional and RunTurnAsync only overwrites it when non-null.
            if (wikipediaInfo?.ConfidenceScore is not > 0.5) return null;

            var enhancedPrompt = $@"
                Based on this Wikipedia information about '{entity.Name}':
                Summary: {wikipediaInfo.WikipediaContent?.Summary}

                Generate ONE surprising, entertaining fact that most people wouldn't know.
                Use ONLY the information above. If it does not support an interesting fact,
                reply with exactly NONE rather than drawing on anything else you know.
                Keep it under 100 characters and make it engaging for movie fans.
                ";
            var result = await chatClient.GetResponseAsync(enhancedPrompt);
            return NullIfNone(result.Text);
        }

        [Description("Gets detailed information about movies/actors for chat context")]
        public async Task<string?> GetChatContext(
            [Description("The exact movie title, actor name or director name to look up")] string entityName,
            [Description("What the name refers to: 'movie', 'actor' or 'director'")] string entityType = "movie")
        {
            var wikipediaInfo = await wikipediaAgent.GetEnhancedInfo(entityName, entityType);

            if (wikipediaInfo == null) return null;

            return JsonSerializer.Serialize(new
            {
                Entity = entityName,
                Summary = wikipediaInfo.WikipediaContent?.Summary,
                Facts = wikipediaInfo.StructuredData?.StructuredFacts,
                Confidence = wikipediaInfo.ConfidenceScore
            });
        }

        /// <summary>
        /// What the user's query is actually about. The <b>type</b> is the load-bearing half:
        /// <see cref="WikipediaSearchAgent.GetEnhancedInfo"/> picks a different Wikidata SPARQL query
        /// per type, and every caller here used to omit it and take the "movie" default. Running the
        /// film query (instance-of: film) against a person's name matches nothing, which zeroed the
        /// confidence score and pushed every actor and director query onto the ungrounded fallback.
        /// </summary>
        private sealed record DetectedEntity(string Name, string Type)
        {
            public static readonly DetectedEntity None = new("NONE", "movie");

            public bool IsNone => string.Equals(Name, "NONE", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<DetectedEntity> DetectEntity(string userQuery)
        {
            var entityDetectionPrompt = $@"
        Analyze the following user query and determine whether it names a specific movie,
        actor/actress, or director.

        Reply with the entity name and its type separated by a single pipe: NAME|TYPE
        TYPE must be exactly one of: movie, actor, director
        For generic queries like 'action movies' or 'comedies', reply with exactly: NONE

        User query: {userQuery}

        Examples:
        Query: 'list movies with tom hanks in 90s' -> Tom Hanks|actor
        Query: 'show me the matrix movies' -> The Matrix|movie
        Query: 'what popular movies came out last year' -> NONE
        Query: 'movies directed by Christopher Nolan' -> Christopher Nolan|director
        ";
            var detectionResult = await chatClient.GetResponseAsync(entityDetectionPrompt);
            return ParseDetectedEntity(detectionResult.Text);
        }

        /// <summary>
        /// The model answers this prompt without a schema, so it quotes things, adds trailing periods
        /// and varies the casing of NONE. The old code compared with a case-sensitive ordinal ==, which
        /// meant a reply of 'NONE' with quotes was treated as a real entity and sent to Wikipedia.
        /// </summary>
        private static DetectedEntity ParseDetectedEntity(string? raw)
        {
            var text = raw?.Trim().Trim('\'', '"', '.', ' ') ?? string.Empty;
            if (text.Length == 0) return DetectedEntity.None;

            var parts = text.Split('|', 2, StringSplitOptions.TrimEntries);
            var name = parts[0].Trim('\'', '"', '.', ' ');

            if (name.Length == 0 || string.Equals(name, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                return DetectedEntity.None;
            }

            // GetEnhancedInfo switches on exactly "movie"/"actor"/"director" and silently falls back to
            // the movie query for anything else, so normalise the near-misses the model actually emits.
            var type = (parts.Length > 1 ? parts[1].Trim('\'', '"', '.', ' ') : "movie").ToLowerInvariant() switch
            {
                "actor" or "actress" or "person" or "cast" => "actor",
                "director" or "filmmaker" => "director",
                _ => "movie",
            };

            return new DetectedEntity(name, type);
        }

        /// <summary>
        /// The grounded prompts are told to answer NONE rather than invent, so that answer has to be
        /// recognised and turned back into a null rather than shown to the user as a fact.
        /// </summary>
        private static string? NullIfNone(string? text)
        {
            var trimmed = text?.Trim();

            return string.IsNullOrWhiteSpace(trimmed)
                || string.Equals(trimmed.Trim('\'', '"', '.', ' '), "NONE", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : trimmed;
        }

        [Description("Provides rich context about movies/actors for enhanced responses")]
        public async Task<string?> GetMovieContext(
            [Description("The user's question, used to work out which movie or person to describe")] string userQuery)
        {
            var entity = await DetectEntity(userQuery);

            if (entity.IsNone) return null;

            var wikipediaInfo = await wikipediaAgent.GetEnhancedInfo(entity.Name, entity.Type);

            if (wikipediaInfo?.ConfidenceScore is not > 0.5) return null;

            var contextPrompt = $@"
            Based on this Wikipedia information about '{entity.Name}':
            Summary: {wikipediaInfo.WikipediaContent?.Summary}
            Facts: {JsonSerializer.Serialize(wikipediaInfo.StructuredData?.StructuredFacts)}

            Provide 1-2 interesting, engaging facts that would make this movie/person sound fascinating to movie fans.
            Focus on surprising details, cultural impact, or behind-the-scenes stories.
            Use ONLY the information above; reply with exactly NONE if it does not support one.
            Keep it concise but engaging.
            ";
            var result = await chatClient.GetResponseAsync(contextPrompt);
            return NullIfNone(result.Text);
        }
    }
}