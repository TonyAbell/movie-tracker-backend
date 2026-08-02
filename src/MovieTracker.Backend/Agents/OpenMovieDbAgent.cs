using Microsoft.Extensions.Configuration;
using OMDbApiNet;
using OMDbApiNet.Model;
using System.Globalization;

namespace MovieTracker.Backend.Agents
{
    public record MovieTrailerInfo(
        string Key,           // YouTube video ID (from TMDb "key" property)
        string Name,          // Video title (from TMDb "name" property) 
        string Site,          // "YouTube" or "Vimeo" (from TMDb "site" property)
        string Type,          // "Trailer", "Teaser", "Clip", etc. (from TMDb "type" property)
        bool Official,        // Official trailer flag (from TMDb "official" property)
        string YouTubeUrl,    // Constructed: https://www.youtube.com/watch?v={Key}
        string EmbedUrl,      // Constructed: https://www.youtube.com/embed/{Key}
        string ThumbnailUrl   // Constructed: https://img.youtube.com/vi/{Key}/maxresdefault.jpg
    );

    /// <summary>
    /// What survives from an OMDb lookup.
    /// <para>
    /// The fields below <see cref="BoxOffice"/> were being deserialized and thrown away on every
    /// single rating call. OMDb returns 38 properties; this agent read six. Meanwhile the system
    /// prompt asks the model for "interesting trivia", "cultural impact" and "behind-the-scenes
    /// facts" - and Awards ("Won 4 Oscars. 159 wins &amp; 220 nominations total") was sitting in the
    /// response body, already paid for, already parsed, and dropped on the floor. Surfacing them
    /// costs no extra HTTP.
    /// </para>
    /// </summary>
    public record MovieRatingResult(
        string Title,
        string Year,
        string ImdbRating,
        string RottenTomatoesRating,
        string MetacriticRating,
        string BoxOffice,
        bool IsSuccess,
        string ErrorMessage = "",
        MovieTrailerInfo? Trailer = null,
        string Rated = "",
        string Runtime = "",
        string Genre = "",
        string Director = "",
        string Writer = "",
        string Actors = "",
        string Awards = "",
        string Plot = "",
        string ImdbVotes = ""
    );

    public record RatingComparisonResult(
        string HighestRatedTitle,
        string HighestRating,
        List<MovieRatingResult> AllMovies,
        bool IsSuccess,
        string ErrorMessage = ""
    );

    public class OpenMovieDbAgent
    {
        private readonly AsyncOmdbClient omdbClient;

        public OpenMovieDbAgent(IConfiguration configuration)
        {
           var apiKey = configuration["OpenMovieDb:Api-Key"] ?? throw new ArgumentNullException("Missing OMDb Api Key");

           omdbClient = new AsyncOmdbClient(apiKey);
        }

        public async Task<MovieRatingResult> GetMovieRatings(string imdbId)
        {
            try
            {
                var movie = await omdbClient.GetItemByIdAsync(imdbId);

                if (movie.Response == "False")
                {
                    return new MovieRatingResult("", "", "", "", "", "", false, $"Movie not found for IMDb ID: {imdbId}");
                }

                var ratings = movie.Ratings;

                return new MovieRatingResult(
                    Title: movie.Title ?? "",
                    Year: movie.Year ?? "",
                    ImdbRating: movie.ImdbRating ?? "N/A",
                    RottenTomatoesRating: GetRatingBySource(ratings, "Rotten Tomatoes"),
                    MetacriticRating: GetRatingBySource(ratings, "Metacritic"),
                    BoxOffice: movie.BoxOffice ?? "N/A",
                    IsSuccess: true,
                    Rated: movie.Rated ?? "",
                    Runtime: movie.Runtime ?? "",
                    Genre: movie.Genre ?? "",
                    Director: movie.Director ?? "",
                    Writer: movie.Writer ?? "",
                    Actors: movie.Actors ?? "",
                    Awards: movie.Awards ?? "",
                    Plot: movie.Plot ?? "",
                    ImdbVotes: movie.ImdbVotes ?? ""
                );
            }
            catch (Exception ex)
            {
                return new MovieRatingResult("", "", "", "", "", "", false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// One OMDb round trip per id, run concurrently. These used to be awaited one at a time inside
        /// a foreach, so comparing five films meant five serial round trips inside a tool call the model
        /// was blocked on. Order is preserved because Task.WhenAll returns results positionally.
        /// </summary>
        private static async Task<List<MovieRatingResult>> GetManyAsync(
            List<string> imdbIds,
            Func<string, Task<MovieRatingResult>> lookup) =>
            [.. await Task.WhenAll(imdbIds.Select(lookup))];

        public async Task<RatingComparisonResult> CompareMovieRatings(List<string> imdbIds)
        {
            try
            {
                var movieRatings = (await GetManyAsync(imdbIds, GetMovieRatings))
                    .Where(rating => rating.IsSuccess)
                    .ToList();

                if (!movieRatings.Any())
                {
                    return new RatingComparisonResult("", "", new List<MovieRatingResult>(), false, "No valid movies found for comparison");
                }

                var highest = movieRatings
                    .Where(m => double.TryParse(m.ImdbRating, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    .OrderByDescending(m => double.Parse(m.ImdbRating, CultureInfo.InvariantCulture))
                    .FirstOrDefault();

                if (highest == null)
                {
                    return new RatingComparisonResult("", "", movieRatings, false, "No movies with valid IMDb ratings found");
                }

                return new RatingComparisonResult(
                    HighestRatedTitle: $"{highest.Title} ({highest.Year})",
                    HighestRating: highest.ImdbRating,
                    AllMovies: movieRatings,
                    IsSuccess: true
                );
            }
            catch (Exception ex)
            {
                return new RatingComparisonResult("", "", new List<MovieRatingResult>(), false, $"Error: {ex.Message}");
            }
        }

        public async Task<List<MovieRatingResult>> FilterMoviesByRating(List<string> imdbIds, double minimumRating)
        {
            var ratings = await GetManyAsync(imdbIds, GetMovieRatings);

            return [.. ratings.Where(rating =>
                rating.IsSuccess
                && double.TryParse(rating.ImdbRating, NumberStyles.Any, CultureInfo.InvariantCulture, out double actualRating)
                && actualRating >= minimumRating)];
        }

        private static string GetRatingBySource(List<Rating> ratings, string sourceName)
        {
            if (ratings == null) return "N/A";

            var rating = ratings.FirstOrDefault(r => r.Source.Contains(sourceName, StringComparison.OrdinalIgnoreCase));
            return rating?.Value ?? "N/A";
        }
    }
}
