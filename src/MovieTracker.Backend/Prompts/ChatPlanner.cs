using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MovieTracker.Backend.Prompts
{
    public class ChatPlanner
    {

        [KernelFunction]
        [Description("Returns instructions on how best to respone to the user")]
        [return: Description("The list of steps to best respond to the user")]
        public async Task<string> GenerateRequiredSteps()
        {
            // Prompt the LLM to generate a list of steps to complete the task
            string prompt = $$"""
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

            // Return the plan back to the agent
            return prompt.ToString();
        }

    }
}
