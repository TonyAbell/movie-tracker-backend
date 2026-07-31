using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;
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
        private async Task SafeProcessMovieAsync(JsonElement movie, List<MovieViewModel> movieItems, TMDbClient client)
        {
            try
            {
                await ProcessMovieAsync(movie, movieItems, client);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to hydrate a movie entry; skipping it");
            }
        }

        private async Task ProcessMovieAsync(JsonElement movie, List<MovieViewModel> movieItems, TMDbClient client)
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

            var dataInBytes = await cache.GetAsync(movieId);
            if (dataInBytes != null)
            {
                var movieViewModel = JsonSerializer.Deserialize<MovieViewModel>(dataInBytes);
                if (movieViewModel != null)
                {
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

                lock (movieItems) // Thread-safe access to shared list
                {
                    movieItems.Add(movieViewModel);
                }

                var movieViewModelBytes = JsonSerializer.SerializeToUtf8Bytes(movieViewModel);
                await cache.SetAsync(movieId, movieViewModelBytes);
            }
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
                var chatMessages = chatSession.ChatHistory;

                if (chatMessages == null)
                {
                    return new BadRequestObjectResult("Chat not found");
                }

                //string? funnyFact = await chatPlanner.GenerateFunnyFact(ask.Input);
                string? funnyFact = await chatPlanner.GenerateEnhancedFunnyFact(ask.Input);

                if (funnyFact != null)
                {
                    chatSession.FunnyFact = funnyFact;
                }

                chatMessages.Add(new AIChatMessage(ChatRole.User, ask.Input));

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

                // session: null runs the agent statelessly - the entire conversation is passed in on
                // every call and the generated messages are appended back onto it. That is exactly what
                // the Semantic Kernel version did with a ChatHistory, and it is what lets the response
                // below be rebuilt by re-walking the whole conversation.
                var result = await agent.RunAsync(chatMessages, null, new ChatClientAgentRunOptions(chatOptions));

                // Includes the intermediate function-call and function-result messages as well as the
                // final answer. They carry no text, so the loop below skips them, but they must be
                // persisted: a tool call without its result is an invalid conversation on the next turn.
                chatMessages.AddRange(result.Messages);

                var responseMessages = new List<ChatMessage>();
                foreach (var messages in chatMessages)
                {
                    var text = messages.Text;
                    if (messages.Role == ChatRole.Assistant && !String.IsNullOrEmpty(text))
                    {
                        text = text.Replace("```json", "").Replace("```", "");
                        List<MovieViewModel> movieItems = new List<MovieViewModel>();

                        try
                        {
                            System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(text);
                            var root = doc.RootElement;
                            var systemMessage = root.GetProperty("SystemMessage").GetString();

                            if (root.TryGetProperty("MovieList", out JsonElement movieList))
                            {
                                var movieListArray = movieList.EnumerateArray();
                                var tasks = new List<Task>();

                                foreach (var movie in movieListArray)
                                {
                                    tasks.Add(SafeProcessMovieAsync(movie, movieItems, client));
                                }

                                await Task.WhenAll(tasks);
                            }

                            AssistantChatMessage assistantChatMessage = new AssistantChatMessage(systemMessage, movieItems);
                            responseMessages.Add(assistantChatMessage);
                        }
                        catch (JsonException)
                        {
                            var systemMessage = "No movies were found";
                            AssistantChatMessage assistantChatMessage = new AssistantChatMessage(systemMessage, movieItems);
                            responseMessages.Add(assistantChatMessage);
                        }
                    }

                    if (messages.Role == ChatRole.User && !String.IsNullOrEmpty(text))
                    {
                        UserChatMessage userChatMessage = new UserChatMessage(text);
                        responseMessages.Add(userChatMessage);
                    }
                }

                await chatSessionRepository.UpdateChatSession(chatId, chatMessages, chatSession.FunnyFact);

                var response = new ChatMessageResponse(chatSession.FunnyFact, responseMessages);
                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                return new BadRequestObjectResult(ex.Message);
            }
        }
    }
}
