using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodeQueue.Infrastructure.Json;

internal static class QueueJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
