using System.Text;
using FluentAssertions;
using Xunit;

namespace Wino.Ipc.Tests;

public class FrameProtocolTests
{
    [Fact]
    public async Task Frame_RoundTrips_TypeCorrelationAndPayload()
    {
        var (client, server) = InMemoryDuplexStream.CreatePair();
        var correlationId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("""{"hello":"world"}""");

        await FrameProtocol.WriteFrameAsync(client, new Frame(FrameType.Request, correlationId, payload));
        var frame = await FrameProtocol.ReadFrameAsync(server);

        frame.Should().NotBeNull();
        frame!.Type.Should().Be(FrameType.Request);
        frame.CorrelationId.Should().Be(correlationId);
        frame.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task Frame_RoundTrips_EmptyPayload()
    {
        var (client, server) = InMemoryDuplexStream.CreatePair();
        var correlationId = Guid.NewGuid();

        await FrameProtocol.WriteFrameAsync(client, new Frame(FrameType.Ping, correlationId));
        var frame = await FrameProtocol.ReadFrameAsync(server);

        frame!.Type.Should().Be(FrameType.Ping);
        frame.Payload.Length.Should().Be(0);
    }

    [Fact]
    public async Task MultipleFrames_AreReadInOrder()
    {
        var (client, server) = InMemoryDuplexStream.CreatePair();

        for (var i = 0; i < 10; i++)
        {
            await FrameProtocol.WriteFrameAsync(client, new Frame(FrameType.Event, Guid.NewGuid(), Encoding.UTF8.GetBytes($"payload-{i}")));
        }

        for (var i = 0; i < 10; i++)
        {
            var frame = await FrameProtocol.ReadFrameAsync(server);
            Encoding.UTF8.GetString(frame!.Payload.Span).Should().Be($"payload-{i}");
        }
    }

    [Fact]
    public void OversizedPayload_IsRejected()
    {
        var payload = new byte[Frame.MaxPayloadLength + 1];
        var construct = () => new Frame(FrameType.Request, Guid.NewGuid(), payload);

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CleanStreamEnd_ReturnsNull()
    {
        var (client, server) = InMemoryDuplexStream.CreatePair();

        client.Dispose();

        var frame = await FrameProtocol.ReadFrameAsync(server);
        frame.Should().BeNull();
    }

    [Fact]
    public async Task TruncatedFrame_Throws()
    {
        var (client, server) = InMemoryDuplexStream.CreatePair();

        // Write only a length prefix promising more data than will ever arrive.
        await client.WriteAsync(new byte[] { 100, 0, 0, 0, 3 });
        client.Dispose();

        var read = async () => await FrameProtocol.ReadFrameAsync(server);
        await read.Should().ThrowAsync<EndOfStreamException>();
    }
}
