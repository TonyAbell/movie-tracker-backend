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

        public async Task<MoviceTrackerChatSession> UpdateChatSession(string id, List<ChatMessage> chatHistory)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-session-repository.update-chat-session");
            try
            {
                var response = await chatHistoryContainer.ReadItemAsync<MoviceTrackerChatSession>(id, new PartitionKey(id));
                MoviceTrackerChatSession movieChatSession = response.Resource;
                movieChatSession.ChatHistory = chatHistory;
                var updateResponse = await chatHistoryContainer.ReplaceItemAsync(movieChatSession, id, new PartitionKey(id));
                return movieChatSession;
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                throw;
            }
        }

        public async Task<MoviceTrackerChatSession> UpdateChatSession(string id, List<ChatMessage> chatHistory, string? funnyFact)
        {
            using var activity = tracer.StartActiveSpan("movie-tracker-func.chat-session-repository.update-chat-session-with-funny-fact");
            try
            {
                var response = await chatHistoryContainer.ReadItemAsync<MoviceTrackerChatSession>(id, new PartitionKey(id));
                MoviceTrackerChatSession movieChatSession = response.Resource;
                movieChatSession.ChatHistory = chatHistory;

                movieChatSession.FunnyFact = funnyFact;

                var updateResponse = await chatHistoryContainer.ReplaceItemAsync(movieChatSession, id, new PartitionKey(id));
                return movieChatSession;
            }
            catch (Exception ex)
            {
                logger.LogCritical("{@ex}", ex);
                throw;
            }
        }

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
