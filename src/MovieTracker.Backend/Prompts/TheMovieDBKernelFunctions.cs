using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TMDbLib.Client;
using TMDbLib.Objects.Discover;

namespace MovieTracker.Backend.Prompts
{
    public class TheMovieDBKernelFunctions(IConfiguration configuration)
    {
        private readonly string apiKey = configuration["TheMovieDb:Api-Key"] ?? throw new ArgumentNullException("Missing The Movice Db Api Key");
        public record MovieItem(string MovieId, string MovieName);

        public record GenresItem(string GenreId, string GenreName);
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
        [Description("Search for people by their name and also known as names.")]
        [return: Description("a json list of people with the following properties PersonId and the PersonName")]
        public async Task<string> SearchForPeople(
                 [Description("The name of the person")] string personName)
        {
            TMDbClient client = new TMDbClient(apiKey);
            var searchResults = await client.SearchPersonAsync(personName,includeAdult:false,region: "en-US");
            var personSearchResults = searchResults.Results.Select(p => new PersonSearchResult(p.Id.ToString(), p.Name)).ToList();
            return JsonSerializer.Serialize(personSearchResults);
        }

        [KernelFunction]
        [Description("Get the top few movie credits for a person by their id.")]
        [return: Description("a json list of movie following properties MovieId and the MovieName")]
        public async Task<string> PersonCreditsBy(
            [Description("The Id for a person: PersonId")] string personId,
            [Description("A date to filter movie credits by, all movies aired greater or equal to this date, can be empty ")] string greaterThanDate,
            [Description("A date to filter movie credits by, all movies aired less or equal to this date, can be empty ")] string lessThanDate)
        {
            TMDbClient client = new TMDbClient(apiKey);
            var credits = await client.GetPersonMovieCreditsAsync(int.Parse(personId));
            var movieRole = credits.Cast.ToList();
            if (greaterThanDate != null)
            {
                movieRole = movieRole.Where(c => c.ReleaseDate >= DateTime.Parse(greaterThanDate)).ToList();
            }
            if (lessThanDate != null)
            {
                movieRole = movieRole.Where(c => c.ReleaseDate <= DateTime.Parse(lessThanDate)).ToList();
            }
            movieRole = movieRole.OrderByDescending(c => c.ReleaseDate).Take(10).ToList();   
            var movieItems = movieRole.Select(c => new MovieItem(c.Id.ToString(), c.Title)).ToList();
            return JsonSerializer.Serialize(movieItems);
        }
       
       
    }
}
