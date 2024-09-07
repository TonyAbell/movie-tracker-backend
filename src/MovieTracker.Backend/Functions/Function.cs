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


    public class Function(Kernel kernel, ChatSessionRepository chatSessionRepository, ILogger<Function> logger, Tracer tracer)
    {



        [Function("Chat-Start")]
        public async Task<IActionResult> Start([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "chat/start")] HttpRequest req)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-start");
            try
            {

                var systemMessage = """
                    You are a friendly assistant who likes to follow the rules. You will complete required steps
                    and request approval before taking any consequential actions. If the user doesn't provide
                    enough information for you to complete a task, you will keep asking questions until you have
                    enough information to complete the task.

                    Return a json object with the following properties:
                    SystemMessage: A message to the user, relavant to their request, if no movies are found, 
                            return a message indicating that no movies were found, and give hints on how best to ask/search for movies
                    
                    MovieList: A list of movies with the following properties MovieId and MovieName, can be an empty list if no movies are found
                    Example:
                    {
                      "SystemMessage": "Here is the list of moves",
                      "MovieList": [
                        {
                          "MovieId": "1",
                          "MovieName": "The Movie"
                        }
                      ]
                    }
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

                        //chatMessages.Add(content);

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
                    //ChatMessage chatMessage;
                    //string role = "";
                    var text = messages.ToString();
                    if (messages.Role == AuthorRole.Assistant && !String.IsNullOrEmpty(text))
                    {
                        //role = "assistant";
                        //todo check to see if text contains the string json.. remove the json string

                        // remove string json if it exists from the text
                        // sometimes the there is ```json in the front of the text
                        // sometimes there is ``` at the end of the text
                        // need to remove these strings

                        text = text.Replace("```json", "");
                        text = text.Replace("```", "");




                        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(text);

                        var root = doc.RootElement;
                        var systemMessage = root.GetProperty("SystemMessage").GetString();
                        var movieList = root.GetProperty("MovieList").EnumerateArray();
                        List<MovieItem> movieItems = new List<MovieItem>();
                        foreach (var movie in movieList)
                        {
                            var movieId = movie.GetProperty("MovieId").GetString();
                            var movieName = movie.GetProperty("MovieName").GetString();
                            movieItems.Add(new MovieItem(movieId, movieName));
                        }

                        AssistantChatMessage assistantChatMessage = new AssistantChatMessage(systemMessage, movieItems);
                        

                        responseMessages.Add(assistantChatMessage);

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

                    //if (!string.IsNullOrEmpty(text))
                    //{
                    //    responseMessages.Add(new ChatMessageRecord(role, text));
                    //}
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
