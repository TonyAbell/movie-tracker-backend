using System.Text.Json;
using TMDbLib.Client;

namespace MovieTracker.Backend.Agents
{
    /// <summary>
    /// Resolves a movie title to its best official YouTube trailer.
    ///
    /// This used to take the user's raw question and run an LLM extraction prompt over it to find the
    /// title, behind a keyword gate that decided whether the question was about trailers at all. Both
    /// are gone, because both were solving a problem at the wrong layer:
    ///
    /// - The extraction prompt only ever saw the single string handed to the tool, never the
    ///   conversation. So "show me the trailer for that one", immediately after the assistant named
    ///   The Dark Knight, extracted UNKNOWN and apologised - the model knew the referent and was told
    ///   to discard it. The caller resolves the title now, which is the only layer that can.
    /// - The keyword gate ("does this contain the word trailer?") was guessing at intent that the model
    ///   has already decided by choosing to call this at all. It also fired on "show me Tom Hanks
    ///   movies" until it was tightened.
    ///
    /// Dropping them removes a completion from the critical path and the IChatClient dependency with it.
    /// </summary>
    public class TrailerAgent(TMDbClient tmdbClient)
    {
        public async Task<string> GetTrailerForTitle(string movieTitle)
        {
            var title = movieTitle?.Trim();

            if (string.IsNullOrWhiteSpace(title) || title.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                return JsonSerializer.Serialize(new
                {
                    Error = "No movie title was supplied.",
                    Hint = "Pass the film's title. If the user referred to it indirectly - 'that one', "
                         + "'it' - use the title from earlier in the conversation; this tool cannot see it."
                });
            }

            var searchResults = await tmdbClient.SearchMovieAsync(title);
            var match = searchResults.Results.FirstOrDefault();

            if (match is null)
            {
                return JsonSerializer.Serialize(new
                {
                    Error = $"No movie found matching '{title}'.",
                    Hint = "Check the title, or call SearchMovies to find the right one first."
                });
            }

            var videos = await tmdbClient.GetMovieVideosAsync(match.Id);

            // Same ordering as ProcessMovieAsync in Function.cs, size included. Without the size
            // tie-break the two paths could pick different trailers for the same film.
            var trailer = videos.Results
                .Where(video => video.Type == "Trailer" && video.Site == "YouTube")
                .OrderByDescending(video => video.Official)
                .ThenByDescending(video => video.Size)
                .FirstOrDefault();

            if (trailer is null)
            {
                return JsonSerializer.Serialize(new
                {
                    MovieId = match.Id.ToString(),
                    MovieName = match.Title,
                    Error = $"No YouTube trailer is available for '{match.Title}'.",
                    Hint = "Say so plainly rather than substituting a different film's trailer."
                });
            }

            return JsonSerializer.Serialize(new
            {
                MovieId = match.Id.ToString(),
                MovieName = match.Title,
                ReleaseDate = match.ReleaseDate?.ToString("yyyy-MM-dd") ?? "",
                TrailerName = trailer.Name,
                // The system prompt requires this wrapper in SystemMessage for the frontend to render
                // the player inline, so it is handed over pre-wrapped rather than described.
                EmbedInSystemMessage = $"[TRAILER]https://www.youtube.com/watch?v={trailer.Key}[/TRAILER]"
            });
        }
    }
}
