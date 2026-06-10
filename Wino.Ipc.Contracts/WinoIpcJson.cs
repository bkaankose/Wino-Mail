using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Wino.Ipc.Contracts;

/// <summary>
/// Central serializer access for everything that crosses the UI ↔ companion pipe:
/// generated request/response records, RPC return values and forwarded UI messages.
///
/// Serialization metadata comes from the source-generated WinoIpcJsonContext that lives in
/// Wino.Ipc.Serialization (a separate assembly, because Roslyn generators cannot chain
/// within one compilation). Both processes must call <see cref="Initialize"/> once at
/// startup before any RPC traffic; unregistered types fail fast instead of silently
/// falling back to reflection, keeping the whole pipe Native-AOT compatible.
/// </summary>
public static class WinoIpcJson
{
    private static JsonSerializerOptions? _options;

    private static readonly ConcurrentDictionary<Type, JsonTypeInfo> TypeInfoCache = new();

    /// <summary>
    /// Installs the source-generated resolver. Called once at process startup by the UI,
    /// the companion and the integration tests.
    /// </summary>
    public static void Initialize(IJsonTypeInfoResolver typeInfoResolver)
    {
        ArgumentNullException.ThrowIfNull(typeInfoResolver);

        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = typeInfoResolver,
        };

        options.MakeReadOnly();
        _options = options;
        TypeInfoCache.Clear();
    }

    public static JsonSerializerOptions Options
        => _options ?? throw new InvalidOperationException(
            "WinoIpcJson is not initialized. Call WinoIpcJson.Initialize(WinoIpcJsonContext.Default) at startup before any RPC traffic.");

    public static JsonTypeInfo<T> GetTypeInfo<T>()
        => (JsonTypeInfo<T>)TypeInfoCache.GetOrAdd(typeof(T), static type =>
            Options.GetTypeInfo(type)
            ?? throw new NotSupportedException(
                $"Type '{type}' is not registered in WinoIpcJsonContext. Add a [JsonSerializable(typeof({type}))] entry (the completeness test prints the exact line)."));

    public static byte[] SerializeToUtf8Bytes<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo<T>());

    public static string SerializeToString<T>(T value)
        => JsonSerializer.Serialize(value, GetTypeInfo<T>());

    public static T? Deserialize<T>(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? default : element.Deserialize(GetTypeInfo<T>());

    public static T? DeserializeFromString<T>(string json)
        => JsonSerializer.Deserialize(json, GetTypeInfo<T>());
}
