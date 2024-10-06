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

namespace MovieTracker.Backend.Functions
{
    public record ChatSessionIdResponse(string ChatId);
    public class Ask
    {
        public string Input { get; set; } = string.Empty;
    }

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

        public List<MovieItem> MovieList { get; set; } = new List<MovieItem>(); 
        public AssistantChatMessage(string text, List<MovieItem> movieList)
        {

            Text = text;
            MovieList = movieList;
        }
    }


    public record ChatMessageResponse(List<ChatMessage> Messages);

    public class MovieListResponse
    {
        public string SystemMessage { get; set; }
        public List<MovieListItem> MovieList { get; set; } 
    }

    public class MovieListItem 
    {
        public string MovieId { get; set; }
        public string MovieName { get; set; }
    }

    public class Function(Kernel kernel, ChatSessionRepository chatSessionRepository, ILogger<Function> logger, Tracer tracer)
    {



        [Function("Chat-Start")]
        public async Task<IActionResult> Start([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "chat/start")] HttpRequest req)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-start");
            try
            {

                var systemMessage = """
                    You are a friendly assistant who follows instructions strictly. Your response must always be a JSON object and nothing else.
                    The JSON object must contain the following properties:

                    - "SystemMessage": A string providing a relevant message to the user. If no movies are found, indicate this and provide hints on how to ask/search for movies.
                    - "MovieList": An array of objects, each having:
                      - "MovieId": A string representing the movie's identifier.
                      - "MovieName": A string representing the movie's name.

                    If you cannot find any movies, return the following JSON object:
                    {
                      "SystemMessage": "No movies were found. Try searching with different keywords.",
                      "MovieList": []
                    }

                    Otherwise, return an object like this:
                    {
                      "SystemMessage": "Here is the list of movies:",
                      "MovieList": [
                        {
                          "MovieId": "1",
                          "MovieName": "The Movie"
                        }
                      ]
                    }
                    Remember to respond only with a JSON object that follows this structure.
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

        [Function("Chat-Ask")]
        public async Task<IActionResult> Message(
                     [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "chat/{chatId}/ask")] HttpRequest req, string chatId)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-ask");
            try
            {
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
                chatMessages.AddUserMessage(ask.Input);


                IChatCompletionService chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

                OpenAIPromptExecutionSettings openAIPromptExecutionSettings = new()
                {
                    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,                  
                };

#pragma warning disable SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                openAIPromptExecutionSettings.ResponseFormat = typeof(MovieListResponse);
#pragma warning restore SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

                var result = await chatCompletionService.GetChatMessageContentsAsync(
                   chatMessages,
                   executionSettings: openAIPromptExecutionSettings,
                   kernel: kernel);

                foreach (var content in result)
                {
                    string role = "";
                    var text = content.ToString();
                    if (content.Role == AuthorRole.Assistant)
                    {
                        role = "assistant";

                        chatMessages.AddAssistantMessage(text);
                    }
                    if (content.Role == AuthorRole.User)
                    {
                        role = "user";
                    }
                }


                var responseMessages = new List<ChatMessage>();
                foreach (var messages in chatMessages)
                {                   
                    var text = messages.ToString();
                    if (messages.Role == AuthorRole.Assistant && !String.IsNullOrEmpty(text))
                    {                       
                        text = text.Replace("```json", "");
                        text = text.Replace("```", "");
                        try
                        {
                            // Validate JSON format
                            System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(text);
                            var root = doc.RootElement;
                            var systemMessage = root.GetProperty("SystemMessage").GetString();
                            JsonElement movieList;
                            List<MovieItem> movieItems = new List<MovieItem>();
                            if (root.TryGetProperty("MovieList", out movieList))
                            {
                                var movieListArray = movieList.EnumerateArray();

                                foreach (var movie in movieListArray)
                                {
                                    var movieId = movie.GetProperty("MovieId").GetString();
                                    var movieName = movie.GetProperty("MovieName").GetString();
                                    movieItems.Add(new MovieItem(movieId, movieName));
                                }
                            }
                            AssistantChatMessage assistantChatMessage = new AssistantChatMessage(systemMessage, movieItems);
                            responseMessages.Add(assistantChatMessage);
                        }
                        catch (JsonException)
                        {
                            // If invalid, prompt the assistant again or rephrase the last message to get a structured response
                            // Or append "Please respond with a properly formatted JSON" to the previous user message
                            List<MovieItem> movieItems = new List<MovieItem>();
                            var systemMessage = "No movies were found";
                            AssistantChatMessage assistantChatMessage = new AssistantChatMessage(systemMessage, movieItems);
                            responseMessages.Add(assistantChatMessage);
                        }

                      

                    }
                    if (messages.Role == AuthorRole.User && !String.IsNullOrEmpty(text))
                    {
                        //role = "user";
                        UserChatMessage userChatMessage = new UserChatMessage(text);
                        responseMessages.Add(userChatMessage);
                    }
                    if (messages.Role == AuthorRole.Tool)
                    {
                        //role = "user";
                        text = "";
                    }
                    if (messages.Role == AuthorRole.System)
                    {
                        //role = "system";
                        text = "";
                    }                 
                }

                await chatSessionRepository.UpdateChatSession(chatId, chatMessages);
                var response = new ChatMessageResponse(responseMessages);

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
