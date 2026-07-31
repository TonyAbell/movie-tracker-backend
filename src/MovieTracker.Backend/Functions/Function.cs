using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using TMDbLib.Client;
using Microsoft.Extensions.Configuration;
using MovieTracker.Backend.Prompts;
using MovieTracker.Backend.Agents;
// This namespace declares its own ChatMessage - the shape returned to the frontend - which would
// otherwise shadow the Microsoft.Extensions.AI one used for conversation state.
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace MovieTracker.Backend.Functions
{
    public record ChatSessionIdResponse(string ChatId);
    public class Ask
    {
        public string Input { get; set; } = string.Empty;
    }
    //    export interface TmdbMovieModel
    //    {
    //        poster_path: string;
    //    adult: boolean;
    //    overview: string;
    //    release_date: string;
    //    genre_ids: number[];
    //    id: string;
    //    original_title: string;
    //    original_language: string;
    //    title: string;
    //    backdrop_path: string;
    //    popularity: number;
    //    vote_count: number;
    //    video: boolean;
    //    vote_average: number;
    //    favorite: boolean;
    //}

    public record MovieViewModel(
        string PosterPath,
        bool Adult,
        string Overview,
        DateTime? ReleaseDate,
        List<int> GenreIds,
        string Id,
        string OriginalTitle,
        string OriginalLanguage,
        string Title,
        string BackdropPath,
        double? Popularity,
        int VoteCount,
        bool Video,
        double VoteAverage,
        bool Favorite,
        string ImdbId,
        MovieTrailerInfo? Trailer
    );
    public record MovieItem(string MovieId, string MovieName);
    public record LLMResponse(string SystemMessage, List<MovieItem> MovieList);
    

    //public record ChatMessageRecord(string Role, string Text);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = nameof(role))]
    [JsonDerivedType(typeof(UserChatMessage), typeDiscriminator: "user")]
    [JsonDerivedType(typeof(AssistantChatMessage), typeDiscriminator: "assistant")]
    public abstract class ChatMessage
    {

     
        public abstract string role { get; }
        public abstract string Text { get; }    
    }

    public class UserChatMessage : ChatMessage
    {
        [JsonIgnore]
        public override string role => "user";        
        public override string Text { get;  }

        public UserChatMessage( string text)
        {
        
            Text = text;
        }
    }

  

    public class AssistantChatMessage : ChatMessage
    {
        [JsonIgnore]
        public override string role => "assistant";
        public override string Text { get; }

        public List<MovieViewModel> MovieList { get; set; } = new List<MovieViewModel>(); 
        public AssistantChatMessage(string text, List<MovieViewModel> movieList)
        {

            Text = text;
            MovieList = movieList;
        }
    }


    public record ChatMessageResponse(string? FunnyFact, List<ChatMessage> Messages);

    // Returned by Chat-Ask-V2 only. The v1 shape replays the entire hydrated transcript on every
    // turn, so its payload and its hydration cost both grow with conversation length even though the
    // caller already has every earlier turn. This carries just the turn that was produced.
    public record ChatTurnResponse(string? FunnyFact, ChatMessage Turn);

    public class ChatSession
    {
        public string Id { get; set; }
        public List<AIChatMessage> ChatHistory { get; set; }
        public string? FunnyFact { get; set; }  // Funny fact at the session level

        public ChatSession(string id, List<AIChatMessage> chatHistory)
        {
            Id = id;
            ChatHistory = chatHistory;
            FunnyFact = null;
        }
    }


    public class MovieListResponse
    {
        public string SystemMessage { get; set; }
        public List<MovieListItem> MovieList { get; set; }
        public string FunnyFact { get; set; }
    }

    public class MovieListItem 
    {
        public string MovieId { get; set; }
        public string MovieName { get; set; }
    }

    public class Function(AIAgent agent, ChatPlanner chatPlanner, ChatSessionRepository chatSessionRepository, IDistributedCache cache, IConfiguration configuration, ILogger<Function> logger, Tracer tracer)
    {
        private readonly string apiKey = configuration["TheMovieDb:Api-Key"] ?? throw new ArgumentNullException("Missing The Movice Db Api Key");

        // The model must answer with MovieListResponse's exact PascalCase property names: the system
        // prompt names "SystemMessage"/"MovieList" and ProcessMovieAsync reads "MovieId" back out.
        // Microsoft.Extensions.AI defaults to camelCase, so the naming policy is pinned to null here -
        // Semantic Kernel's ResponseFormat = typeof(MovieListResponse) used the declared names.
        private static readonly JsonSerializerOptions StructuredOutputJsonOptions =
            new(AIJsonUtilities.DefaultOptions) { PropertyNamingPolicy = null };

        // Semantic Kernel's ResponseFormat = typeof(T) emitted a schema in Azure OpenAI's *strict*
        // dialect. Microsoft.Extensions.AI does not do that by default: ChatResponseFormat.ForJsonSchema
        // (Type, ...) produces a plain schema with no "required" array and no additionalProperties:false,
        // which the service rejects with HTTP 400. These two transform flags restore the strict shape.
        private static readonly ChatResponseFormat MovieListResponseFormat =
            ChatResponseFormat.ForJsonSchema(
                AIJsonUtilities.CreateJsonSchema(
                    typeof(MovieListResponse),
                    serializerOptions: StructuredOutputJsonOptions,
                    inferenceOptions: new AIJsonSchemaCreateOptions
                    {
                        TransformOptions = new AIJsonSchemaTransformOptions
                        {
                            DisallowAdditionalProperties = true,
                            RequireAllProperties = true,
                        }
                    }),
                nameof(MovieListResponse));

        // Reasoning models spend most of their output budget on hidden reasoning tokens, and output
        // tokens are the expensive ones. Measured against this app's own system prompt and tool set,
        // gpt-5-mini emitted 1587 completion tokens per turn of which 1344 were reasoning (~21.7s);
        // the identical turn at "minimal" emitted 75 completion tokens (~3.8s).
        //
        // This stays in configuration rather than hardcoded because the accepted values differ by
        // model family - "minimal" on gpt-5/gpt-5-mini, "none" on gpt-5.1 and later - and models with
        // no reasoning stage at all (grok-4-1-fast-non-reasoning, gpt-oss-120b) reject the parameter
        // outright. Leave the setting unset, empty, or "default" to omit it from the request.
        private readonly string? reasoningEffort = configuration["AzureOpenAi:Reasoning-Effort"];

        // What is STORED and what is SENT are deliberately different. Cosmos keeps every message
        // forever because the /ask contract replays the whole transcript back to the frontend; the
        // model only ever sees this compacted view.
        //
        // Tool results dominate the token count - one DiscoverMovies call returns twenty full movie
        // records - and they are dead weight once the assistant has written its answer, because the
        // ids and titles a follow-up needs ("which of those had the highest rating?") are already in
        // that answer's JSON. ToolResultCompactionStrategy collapses each old assistant-call +
        // tool-result group into a single line recording what was called, and leaves user messages
        // and assistant answers untouched. SlidingWindow is the backstop for genuinely long chats.
        //
        // Compaction always runs over the full stored history, never over its own output, so it never
        // compounds: turn 20 sees exactly the same reduction of turns 1-10 that turn 11 saw.
        private const int MaxModelTurns = 12;

        // MAAI001 gates the whole Microsoft.Agents.AI.Compaction namespace as evaluation-only, the same
        // way OPENAI001 gates ReasoningEffortLevel below. It is the one preview API this project takes a
        // dependency on; the fallback if it changes shape is a hand-rolled filter over the same message
        // list, since nothing outside RunTurnAsync can observe how the context was reduced.
        // How much of an old tool result to keep when its group is collapsed. Enough to show the shape
        // of what came back, not enough to pay for it twice.
        private const int CollapsedToolResultChars = 160;

        // AsChatReducer is the supported way to drive a CompactionStrategy over a plain message list -
        // CompactionMessageIndex, which the strategies actually operate on, is not public. Each
        // ReduceAsync builds its own index from the messages handed in and returns the included ones.
#pragma warning disable MAAI001
        private static readonly IChatReducer ContextReducer = new PipelineCompactionStrategy(
        [
            new ToolResultCompactionStrategy(
                trigger: CompactionTriggers.HasToolCalls(),
                // The current turn's own tool traffic stays verbatim - the model is still reasoning
                // over it - and only earlier turns collapse.
                minimumPreservedGroups: 2)
            {
                ToolCallFormatter = SummariseToolCalls,
            },
            new SlidingWindowCompactionStrategy(
                trigger: CompactionTriggers.TurnsExceed(MaxModelTurns),
                minimumPreservedTurns: 4),
        ]).AsChatReducer();

        // The built-in formatter inlines every tool result verbatim. That is sensible for a result like
        // "Sunny and 72F" and close to useless here, where one DiscoverMovies call returns a twenty-movie
        // JSON array: measured on a synthetic six-turn conversation the default saved 3% of the context,
        // and this saves 68%. What a later turn needs from an old lookup is that it happened and roughly
        // what came back - the ids and titles that mattered are already in the assistant's own answer,
        // which this never touches.
        private static string SummariseToolCalls(CompactionMessageGroup group)
        {
            var results = new Dictionary<string, string>();
            foreach (var result in group.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>())
            {
                results[result.CallId] = result.Result?.ToString() ?? string.Empty;
            }

            var lines = new List<string> { "[Earlier tool calls]" };
            foreach (var call in group.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
            {
                var text = results.TryGetValue(call.CallId, out var result) ? result : string.Empty;

                lines.Add(text.Length <= CollapsedToolResultChars
                    ? $"{call.Name}: {text}"
                    : $"{call.Name}: {text[..CollapsedToolResultChars]}... [{text.Length - CollapsedToolResultChars} more chars elided]");
            }

            return string.Join("\n", lines);
        }
#pragma warning restore MAAI001

        // The per-session hydrated-movie map rides along in the Cosmos document, so it is bounded to
        // keep the document well clear of the 2 MB item limit. A MovieViewModel is ~1-2 KB once the
        // overview and trailer metadata are in it.
        private const int HydratedMovieLimit = 150;

        [Function("Chat-Start")]
        public async Task<IActionResult> Start([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "chat/start")] HttpRequest req)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-start");
            try
            {
                var systemMessage = """
                    You are an enthusiastic movie expert and friendly assistant who loves sharing fascinating insights about films, actors, and directors.

                    When users ask about movies, provide engaging, conversational responses that include:
                    - Interesting trivia and behind-the-scenes facts
                    - Cultural impact and significance
                    - Fun connections between actors, directors, and other films
                    - Engaging descriptions that make movies sound exciting

                    **Looking up movies - this is required, not optional:**
                    - You have functions for searching The Movie DB. Whenever the user asks about movies,
                      actors, directors or genres, CALL THOSE FUNCTIONS to find real titles. Do not answer
                      from memory alone.
                    - NEVER invent a MovieId. Every MovieId you return must be the numeric TMDb id that a
                      function actually returned to you (for example "13", not "1990-Forrest-Gump").
                    - Typical flow: SearchForPeople to resolve a person to a PersonId, then DiscoverMovies
                      with that cast id and any date/genre filters; or SearchMovies when the user names a title.
                    - Populate MovieList with every relevant movie you found. Return an empty MovieList only
                      when the functions genuinely came back with no matches.

                    **Ratings - IMDb is the default:**
                    - When the user asks about a "rating", "score", or which film was "highest rated"
                      without naming a source, they mean the IMDb rating. Call GetMovieRating for one
                      film, or CompareMovieRatings for several, and answer from what it returns.
                    - TMDb search and detail results carry a TmdbVoteAverage. That is not the rating.
                      Never answer a rating question from it, and never present it as "the rating".
                    - Rotten Tomatoes and Metacritic are secondary context; name the source if you cite them.
                    - Report what a function returned in your own words. Never paste raw JSON from a
                      function result into SystemMessage - the user sees that text verbatim.

                    **If the user requests a trailer, preview, teaser, promo, or attraction video, always include the trailer's YouTube link in your response, embedded in-line and wrapped in [TRAILER]...[/TRAILER] tags.**
                    For example: [TRAILER]https://www.youtube.com/watch?v=vc7_mH2PWHs[/TRAILER]

                    Your response must always be a JSON object with these properties:
                    - "SystemMessage": A rich, engaging message (2-4 sentences) that's informative yet entertaining. Use enthusiastic but not over-the-top language. Include interesting details, trivia, or context that makes the movie sound compelling. **If a trailer is available, embed the trailer link in-line using [TRAILER]...[/TRAILER] tags.**
                    - "MovieList": An array of movie objects with MovieId and MovieName properties.

                    Examples of good SystemMessage responses:
                    - "Inception is Nolan's mind-bending masterpiece where dreams have dreams! The rotating hallway fight took 3 weeks to film and Leonardo DiCaprio's spinning top became one of cinema's most iconic props."
                    - "The Matrix revolutionized action cinema with its bullet-time effects and deep philosophical themes. Keanu Reeves trained for 4 months to perform his own stunts, and the green 'code' is actually Japanese sushi recipes!"
                    - "The Batman (2022) reimagines Gotham with a gritty noir style. [TRAILER]https://www.youtube.com/watch?v=vc7_mH2PWHs[/TRAILER]"

                    If no specific movies are found, provide helpful search suggestions in an engaging way:
                    {
                      "SystemMessage": "Hmm, I couldn't find specific movies matching that! Try being more specific - like 'sci-fi movies from 2010' or 'comedies with Ryan Reynolds'. I'm great at finding hidden gems and blockbusters alike!",
                      "MovieList": []
                    }

                    Always respond only with a JSON object. Keep responses informative but concise (2-4 sentences max).
                    """;

                List<AIChatMessage> chatHistory = [new AIChatMessage(ChatRole.System, systemMessage)];
                var newChatSession = await chatSessionRepository.NewChatSession(chatHistory);
                return new OkObjectResult(new ChatSessionIdResponse(newChatSession.id));
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                return new BadRequestObjectResult(ex.Message);
            }
        }

        public MovieTrailerInfo? CreateTrailerInfo(TMDbLib.Objects.General.Video? tmdbVideo)
        {
            if (tmdbVideo == null || tmdbVideo.Site != "YouTube")
                return null;

            return new MovieTrailerInfo(
                Key: tmdbVideo.Key,
                Name: tmdbVideo.Name,
                Site: tmdbVideo.Site,
                Type: tmdbVideo.Type,
                Official: tmdbVideo.Official,
                YouTubeUrl: $"https://www.youtube.com/watch?v={tmdbVideo.Key}",
                EmbedUrl: $"https://www.youtube.com/embed/{tmdbVideo.Key}",
                ThumbnailUrl: $"https://img.youtube.com/vi/{tmdbVideo.Key}/maxresdefault.jpg"
            );
        }

        // These run under Task.WhenAll, so one unresolvable movie would otherwise fail the
        // entire response. Hydration of a single entry is best-effort.
        private async Task SafeProcessMovieAsync(
            JsonElement movie,
            List<MovieViewModel> movieItems,
            TMDbClient client,
            ConcurrentDictionary<string, MovieViewModel> sessionCache)
        {
            try
            {
                await ProcessMovieAsync(movie, movieItems, client, sessionCache);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to hydrate a movie entry; skipping it");
            }
        }

        private async Task ProcessMovieAsync(
            JsonElement movie,
            List<MovieViewModel> movieItems,
            TMDbClient client,
            ConcurrentDictionary<string, MovieViewModel> sessionCache)
        {
            if (!movie.TryGetProperty("MovieId", out var movieIdElement))
            {
                logger.LogWarning("Movie entry has no MovieId");
                return;
            }

            // The model sometimes emits MovieId as a number, and sometimes invents a
            // non-numeric id entirely. Neither should take down the whole response.
            var movieId = movieIdElement.ValueKind == JsonValueKind.Number
                ? movieIdElement.GetRawText()
                : movieIdElement.GetString();

            if (string.IsNullOrWhiteSpace(movieId) || !int.TryParse(movieId, out var tmdbMovieId))
            {
                logger.LogWarning("Skipping movie with non-numeric MovieId '{MovieId}'", movieId);
                return;
            }

            // Tier 1: this session's own document, which is already in memory from the load at the
            // top of the request and survives instance restarts and scale-out. Replayed turns hit
            // here, which is what makes hydration cost O(this turn) rather than O(conversation).
            if (sessionCache.TryGetValue(movieId, out var cachedForSession))
            {
                lock (movieItems)
                {
                    movieItems.Add(cachedForSession);
                }
                return;
            }

            // Tier 2: the process-wide IDistributedCache. Shared across sessions on this instance,
            // but AddDistributedMemoryCache means it is cold on every new instance.
            var dataInBytes = await cache.GetAsync(movieId);
            if (dataInBytes != null)
            {
                var movieViewModel = JsonSerializer.Deserialize<MovieViewModel>(dataInBytes);
                if (movieViewModel != null)
                {
                    sessionCache[movieId] = movieViewModel;
                    lock (movieItems) // Thread-safe access to shared list
                    {
                        movieItems.Add(movieViewModel);
                    }
                }
            }
            else
            {
                var tmdbMovie = await client.GetMovieAsync(tmdbMovieId);
                var imdbId = tmdbMovie.ImdbId ?? "";

                MovieTrailerInfo? trailerInfo = null;
                try
                {
                    var videos = await client.GetMovieVideosAsync(tmdbMovieId);
                    var trailer = videos.Results
                        .Where(v => v.Type == "Trailer" && v.Site == "YouTube")
                        .OrderByDescending(v => v.Official)
                        .ThenByDescending(v => v.Size)
                        .FirstOrDefault();

                    trailerInfo = CreateTrailerInfo(trailer);
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Failed to get trailer for movie {movieId}: {ex.Message}");
                    trailerInfo = null;
                }

                var movieViewModel = new MovieViewModel(
                    tmdbMovie.PosterPath,
                    tmdbMovie.Adult,
                    tmdbMovie.Overview,
                    tmdbMovie.ReleaseDate,
                    tmdbMovie.Genres.Select(g => g.Id).ToList(),
                    movieId,
                    tmdbMovie.OriginalTitle,
                    tmdbMovie.OriginalLanguage,
                    tmdbMovie.Title,
                    tmdbMovie.BackdropPath,
                    tmdbMovie.Popularity,
                    tmdbMovie.VoteCount,
                    tmdbMovie.Video,
                    tmdbMovie.VoteAverage,
                    Favorite: false,
                    imdbId,
                    trailerInfo
                );

                sessionCache[movieId] = movieViewModel;

                lock (movieItems) // Thread-safe access to shared list
                {
                    movieItems.Add(movieViewModel);
                }

                var movieViewModelBytes = JsonSerializer.SerializeToUtf8Bytes(movieViewModel);
                await cache.SetAsync(movieId, movieViewModelBytes);
            }
        }

        // Turns one stored conversation message into the frontend shape, hydrating any movie ids it
        // carries. Returns null for the messages the contract does not surface: the system prompt and
        // the intermediate function-call / function-result messages, which have no text.
        private async Task<ChatMessage?> ToClientMessageAsync(
            AIChatMessage message,
            TMDbClient client,
            ConcurrentDictionary<string, MovieViewModel> sessionCache)
        {
            var text = message.Text;
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (message.Role == ChatRole.User)
            {
                return new UserChatMessage(text);
            }

            if (message.Role != ChatRole.Assistant)
            {
                return null;
            }

            text = text.Replace("```json", "").Replace("```", "");
            List<MovieViewModel> movieItems = new List<MovieViewModel>();

            try
            {
                JsonDocument doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var systemMessage = root.GetProperty("SystemMessage").GetString();

                if (root.TryGetProperty("MovieList", out JsonElement movieList))
                {
                    var tasks = new List<Task>();
                    foreach (var movie in movieList.EnumerateArray())
                    {
                        tasks.Add(SafeProcessMovieAsync(movie, movieItems, client, sessionCache));
                    }

                    await Task.WhenAll(tasks);
                }

                return new AssistantChatMessage(systemMessage, movieItems);
            }
            catch (JsonException)
            {
                return new AssistantChatMessage("No movies were found", movieItems);
            }
        }

        // Decorative, and now racing the model call rather than blocking it, so a Wikipedia outage
        // must not take the turn down with it. Swallowing here also keeps the task from faulting
        // while nothing is awaiting it, which the sequential version could not produce.
        private async Task<string?> SafeGenerateFunnyFactAsync(string input)
        {
            try
            {
                return await chatPlanner.GenerateEnhancedFunnyFact(input);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Funny fact generation failed; answering without one");
                return null;
            }
        }

        // Azure Monitor's token metrics on the Cognitive Services account report 0 and the OpenTelemetry
        // gen_ai.usage spans arrive only partially, so per-turn usage is logged here where it can be
        // read straight out of App Insights. CachedInput is the one to watch: non-zero means the static
        // system prompt, tool schemas and JSON schema are hitting Azure OpenAI's automatic prompt cache;
        // a persistent zero means something started varying that prefix and the discount is being lost.
        private void LogUsage(AgentResponse result)
        {
            if (result.Usage is not { } usage)
            {
                return;
            }

            logger.LogInformation(
                "Chat-Ask usage: input={InputTokens} cachedInput={CachedInputTokens} output={OutputTokens} reasoning={ReasoningTokens} total={TotalTokens}",
                usage.InputTokenCount,
                usage.CachedInputTokenCount,
                usage.OutputTokenCount,
                usage.ReasoningTokenCount,
                usage.TotalTokenCount);
        }

        private static ConcurrentDictionary<string, MovieViewModel> LoadHydrationCache(MoviceTrackerChatSession session) =>
            session.HydratedMovies is null
                ? new ConcurrentDictionary<string, MovieViewModel>()
                : new ConcurrentDictionary<string, MovieViewModel>(session.HydratedMovies);

        private async Task SaveSessionAsync(MoviceTrackerChatSession session, ConcurrentDictionary<string, MovieViewModel> sessionCache)
        {
            if (sessionCache.Count > HydratedMovieLimit)
            {
                // Only a document-size bound. Anything dropped is re-fetched from TMDb the next time
                // it is replayed, so this costs latency on a very long conversation, never correctness.
                logger.LogInformation(
                    "Hydration cache for session {ChatId} holds {Count} movies; trimming to {Limit}",
                    session.id, sessionCache.Count, HydratedMovieLimit);
            }

            session.HydratedMovies = sessionCache
                .Take(HydratedMovieLimit)
                .ToDictionary(entry => entry.Key, entry => entry.Value);

            await chatSessionRepository.SaveChatSession(session);
        }

        // One conversational turn, shared by both routes: they differ only in how much of the
        // conversation they hydrate afterwards. Appends the user message and everything the model
        // produced onto the session's stored history and returns the final assistant message.
        private async Task<AIChatMessage?> RunTurnAsync(MoviceTrackerChatSession chatSession, string input)
        {
            var chatMessages = chatSession.ChatHistory;

            // The funny fact reads nothing but the user's raw input, yet it used to run to completion
            // before the model call even started - an entity-detection completion, a Wikipedia REST
            // fetch, a Wikidata SPARQL query and a second completion, all on the critical path. Kicked
            // off here and collected after the answer comes back.
            Task<string?> funnyFactTask = SafeGenerateFunnyFactAsync(input);

            chatMessages.Add(new AIChatMessage(ChatRole.User, input));

            // The agent already carries the tool set and invokes tools automatically, so only the
            // per-call settings are built here. Run options are merged over the agent's defaults,
            // and tool collections are unioned rather than replaced.
            ChatOptions chatOptions = new()
            {
                ResponseFormat = MovieListResponseFormat,
            };

            if (!string.IsNullOrWhiteSpace(reasoningEffort)
                && !string.Equals(reasoningEffort, "default", StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately not ChatOptions.Reasoning: its ReasoningEffort enum only offers
                // None/Low/Medium/High/ExtraHigh, and this setting has to be able to carry
                // model-family-specific values such as "minimal". Dropping to the provider's own
                // options type keeps the passthrough behaviour SK had.
                // OPENAI001 gates ReasoningEffortLevel as evaluation-only, the same way SKEXP0010
                // gated the structured-output ResponseFormat this method used to set.
#pragma warning disable OPENAI001
                chatOptions.RawRepresentationFactory = _ => new OpenAI.Chat.ChatCompletionOptions
                {
                    ReasoningEffortLevel = new OpenAI.Chat.ChatReasoningEffortLevel(reasoningEffort)
                };
#pragma warning restore OPENAI001
            }

            // Reduced over a copy on purpose. The reducer materialises whatever it is handed into the
            // list its strategies then rewrite in place, and the stored history has to survive intact -
            // if compaction reached it, the /ask transcript would quietly start losing turns.
            var modelMessages = (await ContextReducer.ReduceAsync([.. chatMessages], default)).ToList();

            logger.LogInformation(
                "Chat-Ask context: sending {IncludedMessages} of {TotalMessages} stored messages to the model",
                modelMessages.Count, chatMessages.Count);

            // session: null runs the agent statelessly - the conversation is passed in on every call
            // and the generated messages are appended back onto it. That is what the Semantic Kernel
            // version did with a ChatHistory, and it is what lets the response be rebuilt by
            // re-walking the stored conversation.
            var result = await agent.RunAsync(modelMessages, null, new ChatClientAgentRunOptions(chatOptions));

            LogUsage(result);

            // Includes the intermediate function-call and function-result messages as well as the
            // final answer. They carry no text, so the transcript skips them, but they must be
            // persisted: a tool call without its result is an invalid conversation on the next turn.
            chatMessages.AddRange(result.Messages);

            var funnyFact = await funnyFactTask;
            if (funnyFact != null)
            {
                chatSession.FunnyFact = funnyFact;
            }

            return result.Messages.LastOrDefault(
                message => message.Role == ChatRole.Assistant && !string.IsNullOrEmpty(message.Text));
        }

        [Function("Chat-Ask")]
        public async Task<IActionResult> Message(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "chat/{chatId}/ask")] HttpRequest req,
            string chatId)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-ask");
            try
            {
                TMDbClient client = new TMDbClient(apiKey);
                var ask = await req.ReadFromJsonAsync<Ask>();
                if (ask == null)
                {
                    return new BadRequestObjectResult("Invalid request, missing ask object");
                }

                logger.LogDebug("Chat message received.");
                var chatSession = await chatSessionRepository.GetChatSession(chatId);

                if (chatSession.ChatHistory == null)
                {
                    return new BadRequestObjectResult("Chat not found");
                }

                var sessionCache = LoadHydrationCache(chatSession);
                await RunTurnAsync(chatSession, ask.Input);

                // This route's contract is the whole transcript, every turn. The hydration cache is
                // what keeps that from meaning a TMDb round trip per movie per turn.
                var responseMessages = new List<ChatMessage>();
                foreach (var message in chatSession.ChatHistory)
                {
                    var clientMessage = await ToClientMessageAsync(message, client, sessionCache);
                    if (clientMessage != null)
                    {
                        responseMessages.Add(clientMessage);
                    }
                }

                await SaveSessionAsync(chatSession, sessionCache);

                var response = new ChatMessageResponse(chatSession.FunnyFact, responseMessages);
                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                return new BadRequestObjectResult(ex.Message);
            }
        }

        // Same turn, same persistence, smaller answer: only the turn just produced is hydrated and
        // returned. Additive - /api/chat/{chatId}/ask is untouched - and the two can be interleaved
        // on one session, since both read and write the same stored history.
        [Function("Chat-Ask-V2")]
        public async Task<IActionResult> MessageV2(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "chat/{chatId}/ask/v2")] HttpRequest req,
            string chatId)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-ask-v2");
            try
            {
                TMDbClient client = new TMDbClient(apiKey);
                var ask = await req.ReadFromJsonAsync<Ask>();
                if (ask == null)
                {
                    return new BadRequestObjectResult("Invalid request, missing ask object");
                }

                var chatSession = await chatSessionRepository.GetChatSession(chatId);

                if (chatSession.ChatHistory == null)
                {
                    return new BadRequestObjectResult("Chat not found");
                }

                var sessionCache = LoadHydrationCache(chatSession);
                var newAssistantMessage = await RunTurnAsync(chatSession, ask.Input);

                ChatMessage turn = newAssistantMessage == null
                    ? new AssistantChatMessage("No movies were found", [])
                    : await ToClientMessageAsync(newAssistantMessage, client, sessionCache)
                      ?? new AssistantChatMessage("No movies were found", []);

                await SaveSessionAsync(chatSession, sessionCache);

                return new OkObjectResult(new ChatTurnResponse(chatSession.FunnyFact, turn));
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                return new BadRequestObjectResult(ex.Message);
            }
        }
    }
}
