using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel;
using OpenTelemetry.Trace;

namespace MovieTracker.Backend.Functions
{
    public record ChatSessionIdResponse(string ChatId);
    public class Ask
    {
        public string Input { get; set; } = string.Empty;
    }
    public record ChatMessageRecord(string Role, string Text);
    public record ChatMessageResponse(List<ChatMessageRecord> Messages);

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


                var responseMessages = new List<ChatMessageRecord>();
                foreach (var messages in chatMessages)
                {
                    string role = "";
                    var text = messages.ToString();
                    if (messages.Role == AuthorRole.Assistant)
                    {
                        role = "assistant";

                    }
                    if (messages.Role == AuthorRole.User)
                    {
                        role = "user";
                    }
                    if (messages.Role == AuthorRole.Tool)
                    {
                        role = "user";
                        text = "";
                    }
                    if (messages.Role == AuthorRole.System)
                    {
                        role = "system";
                        text = "";
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        responseMessages.Add(new ChatMessageRecord(role, text));
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
