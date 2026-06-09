using System.Text.Json;
using FluentAssertions;
using Wino.Ipc.Protocol;
using Xunit;

namespace Wino.Ipc.Tests;

public class RpcRoundTripTests
{
    private static byte[] SerializeResponse(TestResponse response)
        => JsonSerializer.SerializeToUtf8Bytes(response, TestJsonContext.Default.TestResponse);

    private static TestRequest DeserializeRequest(JsonElement payload)
        => payload.Deserialize(TestJsonContext.Default.TestRequest)!;

    [Fact]
    public async Task Handshake_WithMatchingProtocolVersion_IsAccepted()
    {
        var handler = new TestRequestHandler();
        var (client, _, _, _) = TestConnectionFactory.Create(handler);

        var response = await client.HandshakeAsync(new HandshakeRequest(TestConnectionFactory.ProtocolVersion, "2.0.0", "ui"));

        response.Accepted.Should().BeTrue();
        response.ProtocolVersion.Should().Be(TestConnectionFactory.ProtocolVersion);
        response.AppVersion.Should().Be("1.0.0-test");
    }

    [Fact]
    public async Task Handshake_WithMismatchedProtocolVersion_IsRejected()
    {
        var handler = new TestRequestHandler();
        var (client, _, _, _) = TestConnectionFactory.Create(handler);

        var response = await client.HandshakeAsync(new HandshakeRequest(TestConnectionFactory.ProtocolVersion + 1, "2.0.0", "ui"));

        response.Accepted.Should().BeFalse();
        response.Message.Should().Contain("mismatch");
    }

    [Fact]
    public async Task Invoke_EchoesTypedRequestAndResponse()
    {
        var handler = new TestRequestHandler();
        handler.Register("Echo", (payload, _) =>
        {
            var request = DeserializeRequest(payload);
            return Task.FromResult<byte[]?>(SerializeResponse(new TestResponse(request.Text, request.Number * 2)));
        });

        var client = await TestConnectionFactory.CreateHandshakenClientAsync(handler);

        var response = await client.InvokeAsync(
            "Echo",
            new TestRequest("hello", 21),
            TestJsonContext.Default.TestRequest,
            TestJsonContext.Default.TestResponse);

        response.Text.Should().Be("hello");
        response.Number.Should().Be(42);
    }

    [Fact]
    public async Task Invoke_VoidMethod_Completes()
    {
        var invoked = false;
        var handler = new TestRequestHandler();
        handler.Register("FireAndAwait", (_, _) =>
        {
            invoked = true;
            return Task.FromResult<byte[]?>(null);
        });

        var client = await TestConnectionFactory.CreateHandshakenClientAsync(handler);

        await client.InvokeAsync("FireAndAwait", new TestRequest("x", 1), TestJsonContext.Default.TestRequest);

        invoked.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentInvocations_CorrelateResponsesCorrectly()
    {
        var handler = new TestRequestHandler();
        handler.Register("Slow", async (payload, ct) =>
        {
            var request = DeserializeRequest(payload);

            // Vary completion order so correlation actually matters.
            await Task.Delay(Random.Shared.Next(1, 40), ct);
            return SerializeResponse(new TestResponse(request.Text, request.Number));
        });

        var client = await TestConnectionFactory.CreateHandshakenClientAsync(handler);

        var calls = Enumerable.Range(0, 64).Select(async i =>
        {
            var response = await client.InvokeAsync(
                "Slow",
                new TestRequest($"call-{i}", i),
                TestJsonContext.Default.TestRequest,
                TestJsonContext.Default.TestResponse);

            response.Text.Should().Be($"call-{i}");
            response.Number.Should().Be(i);
        });

        await Task.WhenAll(calls);
    }

    [Fact]
    public async Task Invoke_LargeResponse_IsChunkedAndReassembled()
    {
        // Force chunking with a small threshold; payload spans many chunks.
        var bigText = new string('w', 300_000);

        var handler = new TestRequestHandler();
        handler.Register("Big", (_, _) => Task.FromResult<byte[]?>(SerializeResponse(new TestResponse(bigText, 7))));

        var client = await TestConnectionFactory.CreateHandshakenClientAsync(handler, chunkThreshold: 16 * 1024);

        var response = await client.InvokeAsync(
            "Big",
            new TestRequest("request", 0),
            TestJsonContext.Default.TestRequest,
            TestJsonContext.Default.TestResponse);

        response.Text.Should().Be(bigText);
        response.Number.Should().Be(7);
    }

    [Fact]
    public async Task Cancellation_MidCall_PropagatesToServerAndThrowsOnClient()
    {
        var serverSawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new TestRequestHandler();
        handler.Register("Hang", async (_, ct) =>
        {
            handlerStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                serverSawCancellation.TrySetResult();
                throw;
            }

            return null;
        });

        var client = await TestConnectionFactory.CreateHandshakenClientAsync(handler);

        using var cts = new CancellationTokenSource();

        var call = client.InvokeAsync(
            "Hang",
            new TestRequest("x", 1),
            TestJsonContext.Default.TestRequest,
            TestJsonContext.Default.TestResponse,
            cancellationToken: cts.Token);

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        await serverSawCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandlerException_SurfacesAsRemoteException()
    {
        var handler = new TestRequestHandler();
        handler.Register("Boom", (_, _) => throw new InvalidOperationException("kaboom"));

        var client = await TestConnectionFactory.CreateHandshakenClientAsync(handler);

        var call = () => client.InvokeAsync(
            "Boom",
            new TestRequest("x", 1),
            TestJsonContext.Default.TestRequest,
            TestJsonContext.Default.TestResponse);

        var exception = (await call.Should().ThrowAsync<WinoRpcRemoteException>()).Which;
        exception.Message.Should().Be("kaboom");
        exception.RemoteExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task DomainExceptionMapper_TakesPrecedence()
    {
        var handler = new TestRequestHandler();
        handler.Register("Boom", (_, _) => throw new InvalidOperationException("mapped"));

        var client = await TestConnectionFactory.CreateHandshakenClientAsync(
            handler,
            domainExceptionMapper: error => error.Message == "mapped" ? new TimeoutException("domain!") : null);

        var call = () => client.InvokeAsync(
            "Boom",
            new TestRequest("x", 1),
            TestJsonContext.Default.TestRequest,
            TestJsonContext.Default.TestResponse);

        await call.Should().ThrowAsync<TimeoutException>().WithMessage("domain!");
    }

    [Fact]
    public async Task DuplicateOperationId_IsDeduplicatedAndReplaysResponse()
    {
        var handler = new TestRequestHandler();
        var executionCount = 0;

        handler.Register("Write", (_, _) =>
        {
            var count = Interlocked.Increment(ref executionCount);
            return Task.FromResult<byte[]?>(SerializeResponse(new TestResponse("executed", count)));
        });

        var deduplicator = new RpcOperationDeduplicator();
        var client = await TestConnectionFactory.CreateHandshakenClientAsync(handler, deduplicator);

        var operationId = Guid.NewGuid();

        var first = await client.InvokeAsync("Write", new TestRequest("x", 1), TestJsonContext.Default.TestRequest, TestJsonContext.Default.TestResponse, operationId);
        var second = await client.InvokeAsync("Write", new TestRequest("x", 1), TestJsonContext.Default.TestRequest, TestJsonContext.Default.TestResponse, operationId);

        executionCount.Should().Be(1);
        first.Number.Should().Be(1);
        second.Number.Should().Be(1);
    }

    [Fact]
    public async Task ConnectionLoss_MidCall_ThrowsConnectionLostException()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new TestRequestHandler();
        handler.Register("Hang", async (_, ct) =>
        {
            handlerStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        });

        var (client, _, _, serverStream) = TestConnectionFactory.Create(handler);
        await client.HandshakeAsync(new HandshakeRequest(TestConnectionFactory.ProtocolVersion, "1.0", "ui"));

        var call = client.InvokeAsync(
            "Hang",
            new TestRequest("x", 1),
            TestJsonContext.Default.TestRequest,
            TestJsonContext.Default.TestResponse);

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Simulate the server process being killed.
        serverStream.Dispose();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<WinoRpcConnectionLostException>();
    }

    [Fact]
    public async Task ServerEvents_ReachTheClient()
    {
        var handler = new TestRequestHandler();
        var (client, server, _, _) = TestConnectionFactory.Create(handler);

        await client.HandshakeAsync(new HandshakeRequest(TestConnectionFactory.ProtocolVersion, "1.0", "ui"));

        var received = new TaskCompletionSource<(string Type, string Json)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (typeName, payload) => received.TrySetResult((typeName, payload.GetRawText()));

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new TestResponse("event!", 5), TestJsonContext.Default.TestResponse);
        server.TryPublishEvent("TestResponse", payloadBytes).Should().BeTrue();

        var (type, json) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        type.Should().Be("TestResponse");
        json.Should().Contain("event!");
    }

    [Fact]
    public async Task Events_BeforeHandshake_AreRejected()
    {
        var handler = new TestRequestHandler();
        var (_, server, _, _) = TestConnectionFactory.Create(handler);

        await Task.Yield();
        server.TryPublishEvent("TestResponse", "{}"u8.ToArray()).Should().BeFalse();
    }

    [Fact]
    public async Task Ping_ReceivesPong()
    {
        var handler = new TestRequestHandler();
        var client = await TestConnectionFactory.CreateHandshakenClientAsync(handler);

        var ping = () => client.PingAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await ping.Should().NotThrowAsync();
    }
}
