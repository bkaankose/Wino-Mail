using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wino.Ipc.Protocol;

namespace Wino.Ipc.Tests;

public sealed record TestRequest(string Text, int Number);

public sealed record TestResponse(string Text, int Number);

[JsonSerializable(typeof(TestRequest))]
[JsonSerializable(typeof(TestResponse))]
public sealed partial class TestJsonContext : JsonSerializerContext;

/// <summary>
/// Request handler with pluggable per-method behavior.
/// </summary>
public sealed class TestRequestHandler : IRpcRequestHandler
{
    private readonly ConcurrentDictionary<string, Func<JsonElement, CancellationToken, Task<byte[]?>>> _handlers = new();

    public int InvocationCount;

    public void Register(string methodName, Func<JsonElement, CancellationToken, Task<byte[]?>> handler)
        => _handlers[methodName] = handler;

    public Task<byte[]?> HandleRequestAsync(string methodName, JsonElement payload, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InvocationCount);

        if (!_handlers.TryGetValue(methodName, out var handler))
            throw new InvalidOperationException($"Unknown method {methodName}.");

        return handler(payload, cancellationToken);
    }
}

public static class TestConnectionFactory
{
    public const int ProtocolVersion = 1;

    public static (RpcClient Client, RpcServerConnection Server, InMemoryDuplexStream ClientStream, InMemoryDuplexStream ServerStream) Create(
        TestRequestHandler handler,
        RpcOperationDeduplicator? deduplicator = null,
        Func<RpcErrorEnvelope, Exception?>? domainExceptionMapper = null,
        int chunkThreshold = 512 * 1024)
    {
        var (clientStream, serverStream) = InMemoryDuplexStream.CreatePair();

        var server = new RpcServerConnection(serverStream, handler, new RpcServerConnectionOptions
        {
            ProtocolVersion = ProtocolVersion,
            AppVersion = "1.0.0-test",
            OperationDeduplicator = deduplicator,
            ChunkThreshold = chunkThreshold,
        });

        server.Start();

        var client = new RpcClient(clientStream, domainExceptionMapper);

        return (client, server, clientStream, serverStream);
    }

    public static async Task<RpcClient> CreateHandshakenClientAsync(TestRequestHandler handler,
                                                                    RpcOperationDeduplicator? deduplicator = null,
                                                                    Func<RpcErrorEnvelope, Exception?>? domainExceptionMapper = null,
                                                                    int chunkThreshold = 512 * 1024)
    {
        var (client, _, _, _) = Create(handler, deduplicator, domainExceptionMapper, chunkThreshold);
        var response = await client.HandshakeAsync(new HandshakeRequest(ProtocolVersion, "1.0.0-test", "tests"));

        if (!response.Accepted)
            throw new InvalidOperationException("Handshake unexpectedly rejected.");

        return client;
    }
}
