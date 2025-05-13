using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.ComponentModel;

namespace MovieTracker.Backend.Prompts
{
    public class ChatPlanner
    {
        private readonly Kernel kernel;

        public ChatPlanner(Kernel kernel)
        {
            this.kernel = kernel;
        }

        [KernelFunction]
        [Description("Detects if user query mentions specific actors/movies and generates a funny fact")]
        [return: Description("A funny fact if entities are detected, null otherwise")]
        public async Task<string?> GenerateFunnyFact(string userQuery)
        {
            // First, detect if the user is asking about specific actors/actresses/movies
            var entityDetectionPrompt = $@"
            Analyze the following user query and determine if it mentions specific:
            1. Actors/actresses by name
            2. Movie titles
            3. Directors
            
            Return ONLY the names of specific entities mentioned, or 'NONE' if no specific entities are found.
            For generic queries like 'action movies' or 'comedies', return 'NONE'.
            
            User query: {userQuery}
            
            Examples:
            Query: 'list movies with tom hanks in 90s' -> 'tom hanks'
            Query: 'show me the matrix movies' -> 'the matrix'
            Query: 'what popular movies came out last year' -> 'NONE'
            Query: 'movies directed by Christopher Nolan' -> 'Christopher Nolan'
            ";

            // Use the kernel to get entity detection
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var detectionResult = await chatService.GetChatMessageContentAsync(entityDetectionPrompt);
            var detectedEntity = detectionResult.Content?.Trim() ?? "NONE";

            // If no entity detected, return null
            if (detectedEntity.ToUpper() == "NONE")
            {
                return null;
            }

            // Generate a funny fact about the detected entity
            var funnyFactPrompt = $@"
            Generate ONE interesting, entertaining, or funny fact about '{detectedEntity}'.
            The fact should be concise, surprising, and relevant to movies or acting if possible.
            Keep it under 100 characters.
            
            Examples:
            - Tom Hanks collects vintage typewriters and owns over 250 of them!
            - The Matrix's famous green code is actually sushi recipes in Japanese.
            - Christopher Nolan doesn't use email or a smartphone.
            ";

            var funnyFactResult = await chatService.GetChatMessageContentAsync(funnyFactPrompt);
            return funnyFactResult.Content?.Trim();
        }

        [KernelFunction]
        [Description("Returns instructions on how best to respond to the user")]
        [return: Description("The list of steps to best respond to the user")]
        public async Task<string> GenerateRequiredSteps()
        {
            // Prompt the LLM to generate a list of steps to complete the task
            string prompt = $$"""
                Return a json object with the following properties:
                SystemMessage: A message to the user, relevant to their request, if no movies are found, 
                        return a message indicating that no movies were found, and give hints on how best to ask/search for movies
                MovieList: A list of movies with the following properties MovieId and MovieName, can be an empty list if no movies are found
                Example:
                {
                  "SystemMessage": "Here is the list of movies",
                  "MovieList": [
                    {
                      "MovieId": "1",
                      "MovieName": "The Movie"
                    }
                  ]
                }
                """;

            // Return the plan back to the agent
            return prompt.ToString();
        }

    }
}
