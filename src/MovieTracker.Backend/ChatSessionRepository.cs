using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MovieTracker.Backend.Functions;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace MovieTracker.Backend
{
    public class MoviceTrackerChatSession
    {
        public string id { get; set; }
        public string PartitionKey { get; set; }

        // Was a Semantic Kernel ChatHistory. The Agent Framework has no equivalent aggregate type -
        // conversation state is either an opaque AgentSession or, as here, a plain message list that
        // the caller owns. The property name is kept so the rest of the Cosmos document shape is
        // unchanged, but the serialized form of the array elements is different, so chat sessions
        // created before this migration cannot be loaded.
        public List<ChatMessage> ChatHistory { get; set; }
        public string? FunnyFact { get; set; }

        // Every movie this session has ever shown, already hydrated from TMDb, keyed by TMDb id.
        // Chat-Ask replays the whole transcript back to the frontend on every call, so without this
        // it re-fetched details + videos for every movie in every past turn - two TMDb round trips
        // per movie, growing with conversation length. The IDistributedCache in front of it is
        // AddDistributedMemoryCache, so it is per-instance and cold after every scale-out or restart;
        // this is the copy that actually survives. Bounded by HydratedMovieLimit in Chat-Ask.
        public Dictionary<string, MovieViewModel>? HydratedMovies { get; set; }
    }

    public class ChatSessionRepository(CosmosClient cosmosClient, ILogger<ChatSessionRepository> logger, Tracer tracer)
    {
        Container chatHistoryContainer = cosmosClient.GetContainer("database", "chat-sessions");

        private static string GenId()
        {
            var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(5)).Replace('/', '~').Replace('+', '-').Replace("=", "");
            if (id.Contains('-') || id.Contains('~'))
            {
                return GenId();
            }
            else
            {
                return id;
            }
        }

        public async Task<MoviceTrackerChatSession> NewChatSession(List<ChatMessage> chatHistory)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-session-repository.new-chat-session");
            try
            {
                var id = GenId();
                var partitionKey = id;
                MoviceTrackerChatSession movieChatSession = new MoviceTrackerChatSession
                {
                    id = id,
                    PartitionKey = partitionKey,
                    ChatHistory = chatHistory,
                    FunnyFact = null  // Initialize with null
                };
                var content = JsonSerializer.Serialize(movieChatSession);
                var response = await chatHistoryContainer.CreateItemAsync<MoviceTrackerChatSession>(movieChatSession, new PartitionKey(partitionKey));
                return movieChatSession;
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                throw;
            }
        }

        public async Task<MoviceTrackerChatSession> GetChatSession(string id)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-session-repository.get-chat-session");
            try
            {
                var response = await chatHistoryContainer.ReadItemAsync<MoviceTrackerChatSession>(id, new PartitionKey(id));
                return response.Resource;
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                throw;
            }
        }

        // Chat-Ask already holds the document it loaded at the top of the request, so it writes the
        // mutated instance straight back. The UpdateChatSession(id, ...) overloads this replaced did a
        // ReadItemAsync first, which meant two Cosmos round trips per /ask to save one turn.
        public async Task<MoviceTrackerChatSession> SaveChatSession(MoviceTrackerChatSession movieChatSession)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-session-repository.save-chat-session");
            try
            {
                var updateResponse = await chatHistoryContainer.ReplaceItemAsync(movieChatSession, movieChatSession.id, new PartitionKey(movieChatSession.PartitionKey));
                return updateResponse.Resource;
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                throw;
            }
        }
    }
}
