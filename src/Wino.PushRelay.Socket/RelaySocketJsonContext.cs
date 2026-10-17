#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wino.PushRelay.Socket;

/// <summary>
/// Every type that crosses the hub as an argument or return value, plus the decrypted payload shape. The SignalR JSON
/// protocol resolves hub arguments through this context, so the Windows Native AOT build never needs reflection.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelaySocketHello))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class RelaySocketJsonContext : JsonSerializerContext;
