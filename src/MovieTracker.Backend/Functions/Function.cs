using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel;
using OpenTelemetry.Trace;
using System.Text.Json.Serialization;
using OpenAI.Chat;
using System.Text.Json;
using static MovieTracker.Backend.Prompts.TheMovieDBKernelFunctions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using TMDbLib.Client;
using Microsoft.Extensions.Configuration;
using MovieTracker.Backend.Prompts;
using MovieTracker.Backend.Agents;

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
        string ImdbId
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
        public ChatHistory ChatHistory { get; set; }
        public string? FunnyFact { get; set; }  // Funny fact at the session level

        public ChatSession(string id, ChatHistory chatHistory)
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

    public class Function(Kernel kernel, ChatSessionRepository chatSessionRepository,IDistributedCache cache, IConfiguration configuration,ILogger<Function> logger, Tracer tracer, WikipediaSearchAgent wikipediaAgent, OpenMovieDbAgent openMovieDbAgent)
    {
        private readonly string apiKey = configuration["TheMovieDb:Api-Key"] ?? throw new ArgumentNullException("Missing The Movice Db Api Key");


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

            Your response must always be a JSON object with these properties:
            - "SystemMessage": A rich, engaging message (2-4 sentences) that's informative yet entertaining. Use enthusiastic but not over-the-top language. Include interesting details, trivia, or context that makes the movie sound compelling.
            - "MovieList": An array of movie objects with MovieId and MovieName properties.

            Examples of good SystemMessage responses:
            - "Inception is Nolan's mind-bending masterpiece where dreams have dreams! The rotating hallway fight took 3 weeks to film and Leonardo DiCaprio's spinning top became one of cinema's most iconic props."
            - "The Matrix revolutionized action cinema with its bullet-time effects and deep philosophical themes. Keanu Reeves trained for 4 months to perform his own stunts, and the green 'code' is actually Japanese sushi recipes!"

            If no specific movies are found, provide helpful search suggestions in an engaging way:
            {
              "SystemMessage": "Hmm, I couldn't find specific movies matching that! Try being more specific - like 'sci-fi movies from 2010' or 'comedies with Ryan Reynolds'. I'm great at finding hidden gems and blockbusters alike!",
              "MovieList": []
            }

            Always respond only with a JSON object. Keep responses informative but concise (2-4 sentences max).
            """;

                ChatHistory chatHistory = new(systemMessage);
                var newChatSession = await chatSessionRepository.NewChatSession(chatHistory);
                return new OkObjectResult(new ChatSessionIdResponse(newChatSession.id));
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                return new BadRequestObjectResult(ex.Message);
            }
        }

        private async Task ProcessMovieAsync(JsonElement movie, List<MovieViewModel> movieItems, TMDbClient client)
        {
            var movieId = movie.GetProperty("MovieId").GetString();
            if (movieId == null)
            {
                logger.LogWarning("MovieId is null");
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
                var tmdbMovie = await client.GetMovieAsync(int.Parse(movieId));
                var imdbId = tmdbMovie.ImdbId ?? "";

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
                    imdbId
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

                var chatPlanner = new ChatPlanner(kernel, wikipediaAgent, openMovieDbAgent);

                // Add the enhanced context function to kernel
                kernel.Plugins.Add(KernelPluginFactory.CreateFromObject(chatPlanner));

                //string? funnyFact = await chatPlanner.GenerateFunnyFact(ask.Input);
                string? funnyFact = await chatPlanner.GenerateEnhancedFunnyFact(ask.Input);

                if (funnyFact != null)
                {
                    chatSession.FunnyFact = funnyFact;
                }

                chatMessages.AddUserMessage(ask.Input);
                IChatCompletionService chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

                OpenAIPromptExecutionSettings openAIPromptExecutionSettings = new()
                {
                    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                };
#pragma warning disable SKEXP0010
                openAIPromptExecutionSettings.ResponseFormat = typeof(MovieListResponse);
#pragma warning restore SKEXP0010

                var result = await chatCompletionService.GetChatMessageContentsAsync(
                    chatMessages,
                    executionSettings: openAIPromptExecutionSettings,
                    kernel: kernel);

                foreach (var content in result)
                {
                    var text = content.ToString();
                    if (content.Role == AuthorRole.Assistant)
                    {
                        chatMessages.AddAssistantMessage(text);
                    }
                }

                var responseMessages = new List<ChatMessage>();
                foreach (var messages in chatMessages)
                {
                    var text = messages.ToString();
                    if (messages.Role == AuthorRole.Assistant && !String.IsNullOrEmpty(text))
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
                                    tasks.Add(ProcessMovieAsync(movie, movieItems, client));
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

                    if (messages.Role == AuthorRole.User && !String.IsNullOrEmpty(text))
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
