using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using TMDbLib.Client;

namespace MovieTracker.Backend.Agents
{
    public class TrailerAgent
    {
        private readonly string apiKey;

        // The bare IChatClient, deliberately not the AIAgent: this is a single-shot extraction prompt
        // that must not inherit the agent's tool set or its JSON response format. Under Semantic
        // Kernel this was kernel.GetRequiredService<IChatCompletionService>() called with no
        // execution settings, which had the same effect.
        private readonly IChatClient chatClient;

        // Words that mean "video content" on their own and nothing else.
        private static readonly string[] TrailerNouns = {
            "trailer", "teaser", "preview", "promo", "featurette", "attraction video"
        };

        // These only count when paired with one of the nouns below. The list used to include "watch",
        // "play" and "show" as standalone triggers, which fired on a large share of perfectly ordinary
        // queries - "show me Tom Hanks movies" routed a plain filmography question into a trailer
        // lookup: an extra completion to extract a title, a TMDb search and a videos call, and an
        // apology string when it found nothing.
        private static readonly string[] PlaybackVerbs = { "watch", "play", "show", "see" };
        private static readonly string[] VideoNouns = { "video", "footage", "clip" };

        public TrailerAgent(IConfiguration configuration, IChatClient chatClient)
        {
            this.apiKey = configuration["TheMovieDb:Api-Key"] ?? throw new ArgumentNullException("Missing The Movie Db Api Key");
            this.chatClient = chatClient;
        }

        public bool CanHandle(string userQuery)
        {
            if (string.IsNullOrWhiteSpace(userQuery)) return false;

            if (TrailerNouns.Any(noun => userQuery.Contains(noun, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // "watch the video", "play that clip" - the verb alone is not evidence of anything.
            return PlaybackVerbs.Any(verb => userQuery.Contains(verb, StringComparison.OrdinalIgnoreCase))
                && VideoNouns.Any(noun => userQuery.Contains(noun, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<string> HandleRequest(string userQuery)
        {
            var movieTitle = await ExtractMovieTitle(userQuery);
            if (string.IsNullOrWhiteSpace(movieTitle))
                return "Sorry, I couldn't determine which movie you want a trailer for.";

            var movieId = await SearchForMovie(movieTitle);
            if (string.IsNullOrWhiteSpace(movieId))
                return $"Sorry, I couldn't find a movie titled '{movieTitle}'.";

            var trailerUrl = await GetTrailerUrl(movieId);
            if (string.IsNullOrWhiteSpace(trailerUrl))
                return $"Sorry, a trailer for '{movieTitle}' could not be found.";

            return $"[TRAILER]{trailerUrl}[/TRAILER]";
        }

        private async Task<string> ExtractMovieTitle(string userQuery)
        {
            var prompt = $@"
                Extract the movie title from: '{userQuery}'
                Return ONLY the movie title, or 'UNKNOWN' if no specific movie is mentioned.
                
                Examples:
                'show me the batman trailer' -> 'The Batman'
                'play inception trailer' -> 'Inception'
                'trailer please' -> 'UNKNOWN'
                ";

            var result = await chatClient.GetResponseAsync(prompt);
            var movieTitle = result.Text?.Trim();

            return movieTitle?.ToUpper() == "UNKNOWN" ? "" : movieTitle ?? "";
        }

        private async Task<string> SearchForMovie(string movieTitle)
        {
            TMDbClient client = new TMDbClient(apiKey);
            var searchResults = await client.SearchMovieAsync(movieTitle);

            return searchResults.Results.FirstOrDefault()?.Id.ToString() ?? "";
        }

        private async Task<string> GetTrailerUrl(string movieId)
        {
            TMDbClient client = new TMDbClient(apiKey);
            var videos = await client.GetMovieVideosAsync(int.Parse(movieId));

            var trailer = videos.Results
                .Where(v => v.Type == "Trailer" && v.Site == "YouTube")
                .OrderByDescending(v => v.Official)
                .FirstOrDefault();

            return trailer != null ? $"https://www.youtube.com/watch?v={trailer.Key}" : "";
        }
    }
}