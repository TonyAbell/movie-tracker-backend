using Microsoft.Extensions.Configuration;
using OMDbApiNet;
using OMDbApiNet.Model;

namespace MovieTracker.Backend.Agents
{
    public record MovieRatingResult(
        string Title,
        string Year,
        string ImdbRating,
        string RottenTomatoesRating,
        string MetacriticRating,
        string BoxOffice,
        bool IsSuccess,
        string ErrorMessage = ""
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
                    IsSuccess: true
                );
            }
            catch (Exception ex)
            {
                return new MovieRatingResult("", "", "", "", "", "", false, $"Error: {ex.Message}");
            }
        }

        public async Task<RatingComparisonResult> CompareMovieRatings(List<string> imdbIds)
        {
            try
            {
                var movieRatings = new List<MovieRatingResult>();

                foreach (var id in imdbIds)
                {
                    var rating = await GetMovieRatings(id);
                    if (rating.IsSuccess)
                    {
                        movieRatings.Add(rating);
                    }
                }

                if (!movieRatings.Any())
                {
                    return new RatingComparisonResult("", "", new List<MovieRatingResult>(), false, "No valid movies found for comparison");
                }

                var highest = movieRatings
                    .Where(m => double.TryParse(m.ImdbRating, out _))
                    .OrderByDescending(m => double.Parse(m.ImdbRating))
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
            var qualifyingMovies = new List<MovieRatingResult>();

            foreach (var id in imdbIds)
            {
                var rating = await GetMovieRatings(id);
                if (rating.IsSuccess &&
                    double.TryParse(rating.ImdbRating, out double actualRating) &&
                    actualRating >= minimumRating)
                {
                    qualifyingMovies.Add(rating);
                }
            }

            return qualifyingMovies;
        }

        private static string GetRatingBySource(List<Rating> ratings, string sourceName)
        {
            if (ratings == null) return "N/A";

            var rating = ratings.FirstOrDefault(r => r.Source.Contains(sourceName, StringComparison.OrdinalIgnoreCase));
            return rating?.Value ?? "N/A";
        }
    }
}
