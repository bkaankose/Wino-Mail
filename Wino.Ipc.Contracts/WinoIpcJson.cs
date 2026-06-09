using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Wino.Ipc.Contracts;

/// <summary>
/// Central serializer configuration for everything that crosses the UI ↔ companion pipe:
/// generated request/response records and forwarded UI messages.
/// </summary>
public static class WinoIpcJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static readonly ConcurrentDictionary<Type, JsonTypeInfo> TypeInfoCache = new();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Tolerate model evolution between slightly mismatched UI/companion builds.
            // The handshake protects against true protocol breaks.
            PropertyNameCaseInsensitive = true,
        };

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static JsonTypeInfo<T> GetTypeInfo<T>()
        => (JsonTypeInfo<T>)TypeInfoCache.GetOrAdd(typeof(T), static type => Options.GetTypeInfo(type));

    public static byte[] SerializeToUtf8Bytes<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo<T>());

    public static T? Deserialize<T>(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? default : element.Deserialize(GetTypeInfo<T>());
}
