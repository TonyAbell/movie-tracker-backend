using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MovieTracker.Backend.Agents
{
    public class WikipediaSearchAgent
    {
        private readonly HttpClient httpClient;
        private readonly ILogger<WikipediaSearchAgent> logger;

        public WikipediaSearchAgent(HttpClient httpClient, ILogger<WikipediaSearchAgent> logger)
        {
            this.httpClient = httpClient;
            this.logger = logger;
        }

        // Reached through ChatPlanner rather than exposed to the model directly - it was never
        // registered as a plugin under Semantic Kernel either. The [Description] attributes are kept
        // so it can be turned into a tool later without rediscovering the wording.
        [Description("Gets enhanced movie/actor information from Wikipedia and Wikidata")]
        [return: Description("Rich information including trivia, context, and detailed facts")]
        public async Task<WikipediaResult?> GetEnhancedInfo(
            [Description("Movie title, actor name, or director name")] string entityName,
            [Description("Type: 'movie', 'actor', or 'director'")] string entityType = "movie")
        {
            try
            {
                var wikipediaData = await SearchWikipedia(entityName);

                var wikidataInfo = await QueryWikidata(entityName, entityType);

                return new WikipediaResult
                {
                    EntityName = entityName,
                    WikipediaContent = wikipediaData,
                    StructuredData = wikidataInfo,
                    ConfidenceScore = CalculateConfidence(wikipediaData, wikidataInfo)
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error fetching Wikipedia data for {EntityName}", entityName);
                return null;
            }
        }

        /// <summary>
        /// One request. It used to be seven: this summary fetch, a second fetch of
        /// <c>/page/sections/{title}</c> whose response was assigned to a local and never read, and then
        /// five fetches of <c>/page/sections/{title}/{section}</c>. That last path is not part of the
        /// Wikipedia REST API - measured, it returns 404 every time - so the section text was always
        /// empty, and its only effect was a confidence bonus that therefore never applied. Six round
        /// trips per funny fact for nothing.
        /// </summary>
        private async Task<WikipediaContent?> SearchWikipedia(string entityName)
        {
            var searchUrl = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(entityName)}";
            var response = await httpClient.GetAsync(searchUrl);

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var summary = JsonSerializer.Deserialize<WikipediaSummary>(content);

            return new WikipediaContent
            {
                Summary = summary?.Extract,
                Thumbnail = summary?.Thumbnail?.Source
            };
        }

        private async Task<WikidataInfo?> QueryWikidata(string entityName, string entityType)
        {
            var sparqlQuery = entityType.ToLower() switch
            {
                "movie" => BuildMovieQuery(entityName),
                "actor" => BuildActorQuery(entityName),
                "director" => BuildDirectorQuery(entityName),
                _ => BuildMovieQuery(entityName)
            };

            var queryUrl = "https://query.wikidata.org/sparql";
            var requestContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("query", sparqlQuery),
                new KeyValuePair<string, string>("format", "json")
            });

            var response = await httpClient.PostAsync(queryUrl, requestContent);
            if (!response.IsSuccessStatusCode) return null;

            var jsonResponse = await response.Content.ReadAsStringAsync();
            return ParseWikidataResponse(jsonResponse, entityType);
        }

        private WikidataInfo? ParseWikidataResponse(string jsonResponse, string entityType)
        {
            try
            {
                var response = JsonSerializer.Deserialize<WikidataResponse>(jsonResponse, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (response?.Results?.Bindings == null || !response.Results.Bindings.Any())
                    return null;

                var facts = new Dictionary<string, object>();
                var relatedEntities = new List<string>();

                foreach (var binding in response.Results.Bindings)
                {
                    switch (entityType.ToLower())
                    {
                        case "movie":
                            ParseMovieBinding(binding, facts, relatedEntities);
                            break;
                        case "actor":
                            ParseActorBinding(binding, facts, relatedEntities);
                            break;
                        case "director":
                            ParseDirectorBinding(binding, facts, relatedEntities);
                            break;
                    }
                }

                return new WikidataInfo
                {
                    StructuredFacts = facts,
                    RelatedEntities = relatedEntities.Distinct().ToList()
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error parsing Wikidata response");
                return null;
            }
        }

        private void ParseMovieBinding(Dictionary<string, WikidataValue> binding, Dictionary<string, object> facts, List<string> relatedEntities)
        {
            if (binding.TryGetValue("director", out var director) && binding.TryGetValue("directorLabel", out var directorLabel))
            {
                facts["Director"] = directorLabel.Value ?? "";
                relatedEntities.Add(directorLabel.Value ?? "");
            }

            if (binding.TryGetValue("releaseDate", out var releaseDate))
            {
                if (DateTime.TryParse(releaseDate.Value, out var date))
                    facts["Release Date"] = date.ToString("yyyy-MM-dd");
            }

            if (binding.TryGetValue("boxOffice", out var boxOffice))
            {
                facts["Box Office"] = boxOffice.Value ?? "";
            }
        }

        private void ParseActorBinding(Dictionary<string, WikidataValue> binding, Dictionary<string, object> facts, List<string> relatedEntities)
        {
            if (binding.TryGetValue("birthDate", out var birthDate))
            {
                if (DateTime.TryParse(birthDate.Value, out var date))
                    facts["Birth Date"] = date.ToString("yyyy-MM-dd");
            }

            if (binding.TryGetValue("birthPlaceLabel", out var birthPlace))
            {
                facts["Birth Place"] = birthPlace.Value ?? "";
            }

            if (binding.TryGetValue("movies", out var movieCount))
            {
                if (int.TryParse(movieCount.Value, out var count))
                    facts["Movie Count"] = count;
            }
        }

        private void ParseDirectorBinding(Dictionary<string, WikidataValue> binding, Dictionary<string, object> facts, List<string> relatedEntities)
        {
            if (binding.TryGetValue("birthDate", out var birthDate))
            {
                if (DateTime.TryParse(birthDate.Value, out var date))
                    facts["Birth Date"] = date.ToString("yyyy-MM-dd");
            }

            if (binding.TryGetValue("birthPlaceLabel", out var birthPlace))
            {
                facts["Birth Place"] = birthPlace.Value ?? "";
            }

            if (binding.TryGetValue("movies", out var movieCount))
            {
                if (int.TryParse(movieCount.Value, out var count))
                    facts["Movies Directed"] = count;
            }

            if (binding.TryGetValue("awards", out var awardCount))
            {
                if (int.TryParse(awardCount.Value, out var count))
                    facts["Awards"] = count;
            }
        }

        private string BuildMovieQuery(string movieTitle)
        {
            return $@"
                SELECT DISTINCT ?item ?itemLabel ?director ?directorLabel ?releaseDate ?boxOffice WHERE {{
                  ?item wdt:P31 wd:Q11424.
                  ?item rdfs:label ""{movieTitle}""@en.
                  OPTIONAL {{ ?item wdt:P57 ?director. }}
                  OPTIONAL {{ ?item wdt:P577 ?releaseDate. }}
                  OPTIONAL {{ ?item wdt:P2142 ?boxOffice. }}
                  SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
                }}
                LIMIT 10";
        }

        // The COUNT subqueries these two used to carry did not project ?item, so SPARQL evaluated them
        // uncorrelated - a global count over every film with any cast member, or any director, computed
        // once per request. Wikidata answered with 504 Gateway Timeout: measured, "Tom Hanks" timed out
        // every time while "Christopher Nolan" happened to squeak through. That timeout was
        // indistinguishable from "this person has no facts", which is how a working lookup ended up
        // scoring below the confidence gate. Birth date and birth place are the facts worth having and
        // they are cheap; the counts are gone.

        private string BuildActorQuery(string actorName)
        {
            return $@"
        SELECT DISTINCT ?item ?itemLabel ?birthDate ?birthPlace ?birthPlaceLabel ?occupation ?occupationLabel WHERE {{
          ?item wdt:P31 wd:Q5.
          ?item rdfs:label ""{actorName}""@en.
          ?item wdt:P106 ?occupation.
          FILTER(?occupation IN (wd:Q33999, wd:Q10800557, wd:Q2259451))
          OPTIONAL {{ ?item wdt:P569 ?birthDate. }}
          OPTIONAL {{ ?item wdt:P19 ?birthPlace. }}
          SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
        }}
        LIMIT 10";
        }

        private string BuildDirectorQuery(string directorName)
        {
            return $@"
        SELECT DISTINCT ?item ?itemLabel ?birthDate ?birthPlace ?birthPlaceLabel WHERE {{
          ?item wdt:P31 wd:Q5.
          ?item rdfs:label ""{directorName}""@en.
          ?item wdt:P106 wd:Q2526255.
          OPTIONAL {{ ?item wdt:P569 ?birthDate. }}
          OPTIONAL {{ ?item wdt:P19 ?birthPlace. }}
          SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
        }}
        LIMIT 10";
        }

        /// <summary>
        /// Scores how much grounding is available, against a &gt; 0.5 gate in ChatPlanner.
        /// <para>
        /// The Wikipedia summary is weighted to clear that gate on its own, because it is the only thing
        /// the callers actually generate from - GenerateEnhancedFunnyFact interpolates
        /// <c>WikipediaContent.Summary</c> and nothing else. The old weights made a substantial summary
        /// worth 0.4 and required Wikidata to bind as well, so a perfectly groundable entity was rejected
        /// whenever the SPARQL endpoint was slow or the labels did not match exactly. That is gating on
        /// data the prompt never reads. Wikidata facts still add confidence and still reach
        /// GetChatContext and GetMovieContext, which do use them.
        /// </para>
        /// <para>
        /// The section bonus is gone with the sections themselves - that endpoint always 404'd, so the
        /// term was dead weight.
        /// </para>
        /// </summary>
        private double CalculateConfidence(WikipediaContent? wikipedia, WikidataInfo? wikidata)
        {
            var score = 0.0;
            if (wikipedia?.Summary?.Length > 100) score += 0.6;
            if (wikidata?.StructuredFacts?.Any() == true) score += 0.4;
            return Math.Min(score, 1.0);
        }
    }

    public class WikipediaResult
    {
        public string EntityName { get; set; } = "";
        public WikipediaContent? WikipediaContent { get; set; }
        public WikidataInfo? StructuredData { get; set; }
        public double ConfidenceScore { get; set; }
    }

    public class WikipediaContent
    {
        public string? Summary { get; set; }
        public string? Thumbnail { get; set; }
    }

    public class WikidataInfo
    {
        public Dictionary<string, object>? StructuredFacts { get; set; }
        public List<string>? RelatedEntities { get; set; }
    }

    /// <summary>
    /// Wikipedia's REST summary payload.
    /// <para>
    /// The <see cref="JsonPropertyNameAttribute"/>s are load-bearing, not tidiness. Wikipedia returns
    /// lowercase keys ("extract", "thumbnail"), System.Text.Json matches case-sensitively by default,
    /// and <c>SearchWikipedia</c> deserialized with no options at all - so <c>Extract</c> bound to
    /// nothing and came back null on every request the app has ever made. That put the confidence score
    /// permanently below ChatPlanner's 0.5 gate, which is what sent every funny fact to the ungrounded
    /// fallback. Measured: null as shipped, 485 chars with these attributes.
    /// </para>
    /// <para>
    /// Reaching for <c>PropertyNameCaseInsensitive = true</c> instead does not work - it makes
    /// "pageid", a JSON number, bind to a string property and throw, and GetEnhancedInfo's catch
    /// swallows that into the same silent null. The offending property was unread, so it is gone.
    /// </para>
    /// </summary>
    public class WikipediaSummary
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("extract")]
        public string? Extract { get; set; }

        [JsonPropertyName("thumbnail")]
        public WikipediaThumbnail? Thumbnail { get; set; }
    }

    public class WikipediaThumbnail
    {
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }
    }

    public class WikidataResponse
    {
        public WikidataHead? Head { get; set; }
        public WikidataResults? Results { get; set; }
    }

    public class WikidataHead
    {
        public List<string>? Vars { get; set; }
    }

    public class WikidataResults
    {
        public List<Dictionary<string, WikidataValue>>? Bindings { get; set; }
    }

    public class WikidataValue
    {
        public string? Type { get; set; }
        public string? Value { get; set; }
        public string? DataType { get; set; }
    }
}
