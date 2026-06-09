using System.Text.Json.Serialization;

namespace Wino.Ipc.Protocol;

/// <summary>
/// Source generated serialization context for protocol-internal payloads.
/// Service request/response payloads are serialized by the generated contracts context instead.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HandshakeRequest))]
[JsonSerializable(typeof(HandshakeResponse))]
[JsonSerializable(typeof(RpcErrorEnvelope))]
public sealed partial class IpcProtocolJsonContext : JsonSerializerContext;
