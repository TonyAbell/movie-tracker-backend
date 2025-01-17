using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Quic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TMDbLib.Client;
using TMDbLib.Objects.Discover;

namespace MovieTracker.Backend.Prompts
{
    public record MovieSearchResult(string MovieId, string MovieName, string ReleaseDate);
    public record GenresItem(string GenreId, string GenreName);
    public class TheMovieDBKernelFunctions(IConfiguration configuration)
    {
        private readonly string apiKey = configuration["TheMovieDb:Api-Key"] ?? throw new ArgumentNullException("Missing The Movice Db Api Key");
        private record MovieItem(string MovieId, string MovieName);

       
        [KernelFunction]
        [Description("Get the list of official genres for movies.")]
        [return: Description("a json list of official genres for movies, with the following properties GenreId and the GenreName")]
        public async Task<string> GetGenresList()
        {
            TMDbClient client = new TMDbClient(apiKey);
            var genres = await client.GetMovieGenresAsync();  
            var genresList = genres.Select(g => new GenresItem(g.Id.ToString(), g.Name)).ToList();
            return JsonSerializer.Serialize(genresList);
        }

        public record PersonSearchResult(string PersonId, string PersonName);
        [KernelFunction]
        [Description("Search for people / cast by their name and also known as names.")]
        [return: Description("a json list of people with the following properties PersonId and the PersonName")]
        public async Task<string> SearchForPeople(
                 [Description("The name of the person or cast member")] string personName)
        {
            TMDbClient client = new TMDbClient(apiKey);
            var searchResults = await client.SearchPersonAsync(personName,includeAdult:false,region: "en-US");
            var personSearchResults = searchResults.Results.Select(p => new PersonSearchResult(p.Id.ToString(), p.Name)).ToList();
            return JsonSerializer.Serialize(personSearchResults);
        }

       

        [KernelFunction]
        [Description("Search for movies by their title and release year. Use this to find movies, you can search by movie name or part of a movie name")]
        [return: Description("a json list of movies with the following properties MovieId, MovieName, and ReleaseDate")]
        public async Task<string> SearchMovies(
            [Description("The title of the movie, or part of the title")] string movieTitle,
            [Description("Optional: The year the movie was released")] string? releaseYear = null)
        {
            TMDbClient client = new TMDbClient(apiKey);
            var yearAsInt = int.Parse(releaseYear ?? "0");
            var searchResults = await client.SearchMovieAsync(movieTitle, year: yearAsInt);

            var movieSearchResults = searchResults.Results
                .Select(m => new MovieSearchResult(m.Id.ToString(), m.Title, m.ReleaseDate.ToString()))
                .ToList();

            return JsonSerializer.Serialize(movieSearchResults);
        }

        [KernelFunction]
        [Description("Get detailed information about a specific movie by its ID.")]
        [return: Description("Detailed information about the movie, including title, overview, release date, genres, and runtime.")]
        public async Task<string> GetMovieDetails(
        [Description("The ID of the movie")] string movieId)
        {
            TMDbClient client = new TMDbClient(apiKey);
            var movie = await client.GetMovieAsync(int.Parse(movieId));
            return JsonSerializer.Serialize(movie);
        }
        [KernelFunction]
        [Description("Search for keywords related to movies.")]
        [return: Description("A JSON list of keywords with their properties such as KeywordId and Name.")]
        public async Task<string> SearchKeywords(
        [Description("The name or partial name of the keyword")] string keyword)
        {
            TMDbClient client = new TMDbClient(apiKey);
            var keywords = await client.SearchKeywordAsync(keyword);
            var keywordList = keywords.Results.Select(k => new { KeywordId = k.Id, Name = k.Name }).ToList();
            return JsonSerializer.Serialize(keywordList);
        }

        [KernelFunction]
        [Description("Discover movies based on various filters and sort options.")]
        [return: Description("A JSON list of movies with their properties such as MovieId, MovieName, and ReleaseDate.")]
        public async Task<string> DiscoverMovies(
          [Description("Optional: Start release date (YYYY-MM-DD)")] string? releaseDateFrom = null,
          [Description("Optional: End release date (YYYY-MM-DD)")] string? releaseDateTo = null,
          [Description("Optional: Include movies with these cast IDs (comma-separated)")] string? castIds = null,
          [Description("Optional: Include movies with these genre IDs (comma-separated)")] string? genreIds = null,
          [Description("Optional: Include movies with these keyword IDs (comma-separated)")] string? keywordIds = null,
          [Description("Optional: Minimum vote average (1-10)")] double? minVoteAverage = null,
          [Description("Optional: Maximum vote average (1-10)")] double? maxVoteAverage = null,
          [Description("Optional: Minimum vote count")] int? minVoteCount = null,
          [Description("Optional: Maximum vote count")] int? maxVoteCount = null
      )
        {
            TMDbClient client = new TMDbClient(apiKey);

            DiscoverMovie query = client.DiscoverMoviesAsync();

            // Apply release date filters
            if (!string.IsNullOrEmpty(releaseDateFrom))
            {
                var releaseDate = DateTime.Parse(releaseDateFrom);
                query = query.WherePrimaryReleaseDateIsAfter(releaseDate);
            }

            if (!string.IsNullOrEmpty(releaseDateTo))
            {
                var releaseDate = DateTime.Parse(releaseDateTo);
                query = query.WherePrimaryReleaseDateIsBefore(releaseDate);
            }

            // Apply cast filters
            if (!string.IsNullOrEmpty(castIds))
            {
                var castIdList = castIds.Split(',').Select(int.Parse);
                query = query.IncludeWithAllOfCast(castIdList);
            }

            // Apply genre filters
            if (!string.IsNullOrEmpty(genreIds))
            {
                var genreIdList = genreIds.Split(',').Select(int.Parse);
                query = query.IncludeWithAllOfGenre(genreIdList);
            }

            // Apply keyword filters
            if (!string.IsNullOrEmpty(keywordIds))
            {
                var keywordIdList = keywordIds.Split(',').Select(int.Parse);
                query = query.IncludeWithAllOfKeywords(keywordIdList);
            }

            // Apply vote average filters
            if (minVoteAverage.HasValue)
            {
                query = query.WhereVoteAverageIsAtLeast(minVoteAverage.Value);
            }

            if (maxVoteAverage.HasValue)
            {
                query = query.WhereVoteAverageIsAtMost(maxVoteAverage.Value);
            }

            // Apply vote count filters
            if (minVoteCount.HasValue)
            {
                query = query.WhereVoteCountIsAtLeast(minVoteCount.Value);
            }

            if (maxVoteCount.HasValue)
            {
                query = query.WhereVoteCountIsAtMost(maxVoteCount.Value);
            }

            // Apply sort criteria
            //if (!string.IsNullOrEmpty(sortBy))
            //{
            //    query = query.OrderBy((DiscoverMovieSortBy)Enum.Parse(typeof(DiscoverMovieSortBy), sortBy, true));
            //}
          
           
       
            // Execute query and format results
            var searchResults = await query.Query();
            var movieList = searchResults.Results.Select(m => new MovieSearchResult(m.Id.ToString(), m.Title, m.ReleaseDate.ToString())).ToList();

            return JsonSerializer.Serialize(movieList);
        }


    }
}
