using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MovieTracker.Backend
{
    public class CosmosSystemTextJsonSerializer : CosmosSerializer
    {
        private JsonSerializerOptions _serializerOptions;

        public CosmosSystemTextJsonSerializer(JsonSerializerOptions serializerOptions = null)
        {
            _serializerOptions = serializerOptions ?? new JsonSerializerOptions
            {
               
            };
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
