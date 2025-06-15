using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;

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

        [KernelFunction]
        [Description("Gets enhanced movie/actor information from Wikipedia and Wikidata")]
        [return: Description("Rich information including trivia, context, and detailed facts")]
        public async Task<WikipediaResult?> GetEnhancedInfo(
            [Description("Movie title, actor name, or director name")] string entityName,
            [Description("Type: 'movie', 'actor', or 'director'")] string entityType = "movie")
        {
            try
            {
                var wikipediaData = await SearchWikipedia(entityName, entityType);

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

        private async Task<WikipediaContent?> SearchWikipedia(string entityName, string entityType)
        {
            // Search for the page
            var searchUrl = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(entityName)}";
            var response = await httpClient.GetAsync(searchUrl);

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var summary = JsonSerializer.Deserialize<WikipediaSummary>(content);

            // Get full content for specific sections
            var sectionsUrl = $"https://en.wikipedia.org/api/rest_v1/page/sections/{Uri.EscapeDataString(entityName)}";
            var sectionsResponse = await httpClient.GetAsync(sectionsUrl);

            return new WikipediaContent
            {
                Summary = summary?.Extract,
                Thumbnail = summary?.Thumbnail?.Source,
                Sections = await ExtractRelevantSections(entityName, entityType)
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

        private async Task<Dictionary<string, string>?> ExtractRelevantSections(string entityName, string entityType)
        {
            try
            {
                var sectionsToExtract = entityType.ToLower() switch
                {
                    "movie" => new[] { "Plot", "Production", "Reception", "Legacy", "Box office" },
                    "actor" => new[] { "Early life", "Career", "Personal life", "Filmography" },
                    "director" => new[] { "Early life", "Career", "Style", "Filmography", "Awards" },
                    _ => new[] { "Plot", "Production", "Reception" }
                };

                var sections = new Dictionary<string, string>();

                foreach (var sectionName in sectionsToExtract)
                {
                    var sectionUrl = $"https://en.wikipedia.org/api/rest_v1/page/sections/{Uri.EscapeDataString(entityName)}/{Uri.EscapeDataString(sectionName)}";

                    try
                    {
                        var response = await httpClient.GetAsync(sectionUrl);
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync();
                            var sectionData = JsonSerializer.Deserialize<WikipediaSection>(content);

                            if (!string.IsNullOrEmpty(sectionData?.Text))
                            {
                                // Clean up the text (remove citations, excess whitespace)
                                var cleanText = CleanWikipediaText(sectionData.Text);
                                if (cleanText.Length > 50) // Only include substantial content
                                {
                                    sections[sectionName] = cleanText;
                                }
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                return sections.Any() ? sections : null;
            }
            catch
            {
                return null;
            }
        }

        private string CleanWikipediaText(string text)
        {
            // Remove citation markers like [1], [2], etc.
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[\d+\]", "");

            // Remove excessive whitespace
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            // Trim and limit length for chat context
            text = text.Trim();
            if (text.Length > 500)
            {
                text = text.Substring(0, 497) + "...";
            }

            return text;
        }

        public class WikipediaSection
        {
            public string? Text { get; set; }
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

        private string BuildActorQuery(string actorName)
        {
            return $@"
        SELECT DISTINCT ?item ?itemLabel ?birthDate ?birthPlace ?birthPlaceLabel ?occupation ?occupationLabel ?movies WHERE {{
          ?item wdt:P31 wd:Q5.
          ?item rdfs:label ""{actorName}""@en.
          ?item wdt:P106 ?occupation.
          FILTER(?occupation IN (wd:Q33999, wd:Q10800557, wd:Q2259451))
          OPTIONAL {{ ?item wdt:P569 ?birthDate. }}
          OPTIONAL {{ ?item wdt:P19 ?birthPlace. }}
          OPTIONAL {{ 
            SELECT (COUNT(?movie) as ?movies) WHERE {{
              ?movie wdt:P31 wd:Q11424.
              ?movie wdt:P161 ?item.
            }}
          }}
          SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
        }}
        LIMIT 10";
        }

        private string BuildDirectorQuery(string directorName)
        {
            return $@"
        SELECT DISTINCT ?item ?itemLabel ?birthDate ?birthPlace ?birthPlaceLabel ?movies ?awards WHERE {{
          ?item wdt:P31 wd:Q5.
          ?item rdfs:label ""{directorName}""@en.
          ?item wdt:P106 wd:Q2526255.
          OPTIONAL {{ ?item wdt:P569 ?birthDate. }}
          OPTIONAL {{ ?item wdt:P19 ?birthPlace. }}
          OPTIONAL {{ 
            SELECT (COUNT(?movie) as ?movies) WHERE {{
              ?movie wdt:P31 wd:Q11424.
              ?movie wdt:P57 ?item.
            }}
          }}
          OPTIONAL {{ 
            SELECT (COUNT(?award) as ?awards) WHERE {{
              ?item wdt:P166 ?award.
            }}
          }}
          SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
        }}
        LIMIT 10";
        }

        private double CalculateConfidence(WikipediaContent? wikipedia, WikidataInfo? wikidata)
        {
            var score = 0.0;
            if (wikipedia?.Summary?.Length > 100) score += 0.4;
            if (wikidata?.StructuredFacts?.Any() == true) score += 0.4;
            if (wikipedia?.Sections?.Any() == true) score += 0.2;
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
        public Dictionary<string, string>? Sections { get; set; }
    }

    public class WikidataInfo
    {
        public Dictionary<string, object>? StructuredFacts { get; set; }
        public List<string>? RelatedEntities { get; set; }
    }

    public class WikipediaSummary
    {
        public string? Title { get; set; }
        public string? Extract { get; set; }
        public WikipediaThumbnail? Thumbnail { get; set; }
        public string? PageId { get; set; }
    }

    public class WikipediaThumbnail
    {
        public string? Source { get; set; }
        public int Width { get; set; }
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
