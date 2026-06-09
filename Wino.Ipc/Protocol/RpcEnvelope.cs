using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Wino.Ipc.Protocol;

/// <summary>
/// Hand-rolled (AOT-safe) reader/writer for the request and event envelopes so the inner
/// payload can be serialized with whatever source generated type info the caller owns.
/// Request: <c>{"m":"IMailService.GetMailsAsync","o":"optional-operation-guid","p":{...}}</c>
/// Event:   <c>{"t":"MailAddedMessage","p":{...}}</c>
/// </summary>
public static class RpcEnvelope
{
    public static byte[] WriteRequest<T>(string methodName, Guid? operationId, T payload, JsonTypeInfo<T> payloadTypeInfo)
    {
        using var bufferStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bufferStream))
        {
            writer.WriteStartObject();
            writer.WriteString("m", methodName);

            if (operationId.HasValue)
                writer.WriteString("o", operationId.Value);

            writer.WritePropertyName("p");
            JsonSerializer.Serialize(writer, payload, payloadTypeInfo);
            writer.WriteEndObject();
        }

        return bufferStream.ToArray();
    }

    public static RpcRequestEnvelope ParseRequest(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;

        var method = root.GetProperty("m").GetString()
            ?? throw new InvalidDataException("Request envelope has no method name.");

        Guid? operationId = root.TryGetProperty("o", out var operationElement)
            ? operationElement.GetGuid()
            : null;

        var payload = root.TryGetProperty("p", out var payloadElement)
            ? payloadElement.Clone()
            : default;

        return new RpcRequestEnvelope(method, operationId, payload);
    }

    public static byte[] WriteEvent(string eventTypeName, ReadOnlyMemory<byte> payloadUtf8Json)
    {
        using var bufferStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bufferStream))
        {
            writer.WriteStartObject();
            writer.WriteString("t", eventTypeName);
            writer.WritePropertyName("p");

            using var payloadDocument = JsonDocument.Parse(payloadUtf8Json);
            payloadDocument.RootElement.WriteTo(writer);

            writer.WriteEndObject();
        }

        return bufferStream.ToArray();
    }

    public static byte[] WriteEvent<T>(string eventTypeName, T payload, JsonTypeInfo<T> payloadTypeInfo)
    {
        using var bufferStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bufferStream))
        {
            writer.WriteStartObject();
            writer.WriteString("t", eventTypeName);
            writer.WritePropertyName("p");
            JsonSerializer.Serialize(writer, payload, payloadTypeInfo);
            writer.WriteEndObject();
        }

        return bufferStream.ToArray();
    }

    public static RpcEventEnvelope ParseEvent(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;

        var typeName = root.GetProperty("t").GetString()
            ?? throw new InvalidDataException("Event envelope has no type name.");

        var payload = root.TryGetProperty("p", out var payloadElement)
            ? payloadElement.Clone()
            : default;

        return new RpcEventEnvelope(typeName, payload);
    }
}

public readonly record struct RpcRequestEnvelope(string MethodName, Guid? OperationId, JsonElement Payload);

public readonly record struct RpcEventEnvelope(string EventTypeName, JsonElement Payload);
