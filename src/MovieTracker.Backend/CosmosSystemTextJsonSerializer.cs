using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace MovieTracker.Backend
{
    public class CosmosSystemTextJsonSerializer : CosmosSerializer
    {
        private JsonSerializerOptions _serializerOptions;

        /// <summary>
        /// Chat sessions store Microsoft.Extensions.AI ChatMessage objects, whose Contents property is
        /// a polymorphic IList&lt;AIContent&gt;. Round-tripping those requires the type resolver from
        /// AIJsonUtilities.DefaultOptions, which knows the $type discriminators for every AIContent
        /// subclass; plain reflection-based options deserialize them back as the abstract base and
        /// throw. It is combined with the default reflection resolver so the surrounding Cosmos
        /// document POCO still serializes normally, and the naming policy is pinned to null (PascalCase)
        /// so that document's existing property names - notably PartitionKey - do not change.
        /// </summary>
        private static JsonSerializerOptions CreateDefaultOptions() => new(AIJsonUtilities.DefaultOptions)
        {
            PropertyNamingPolicy = null,
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                AIJsonUtilities.DefaultOptions.TypeInfoResolver,
                new DefaultJsonTypeInfoResolver()),
        };

        public CosmosSystemTextJsonSerializer(JsonSerializerOptions serializerOptions = null)
        {
            _serializerOptions = serializerOptions ?? CreateDefaultOptions();
        }

        public override T FromStream<T>(Stream stream)
        {
            if (stream == null || stream.CanRead == false)
            {
                return default(T);
            }

            using (stream)
            {
                if (typeof(Stream).IsAssignableFrom(typeof(T)))
                {
                    return (T)(object)stream;
                }

                return JsonSerializer.Deserialize<T>(stream, _serializerOptions);
            }
        }

        public override Stream ToStream<T>(T input)
        {
            MemoryStream streamPayload = new MemoryStream();
            using (Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter(streamPayload, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(utf8JsonWriter, input, _serializerOptions);
            }
            streamPayload.Position = 0;
            return streamPayload;
        }
    }
}
