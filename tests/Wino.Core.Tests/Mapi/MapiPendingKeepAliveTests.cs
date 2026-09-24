using System.Net.Http;
using System.Text;
using Wino.Mapi.Transport;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// Waiting for a reply the server is still writing.
///
/// MS-OXCMAPIHTTP says a slow operation announces itself: while the server works it writes PENDING
/// keep-alives into the response body, and X-PendingPeriod says how often they will come. Measuring
/// the wait with a flat clock instead ended a save at sixty seconds while the server was still
/// working on it - and an abandoned request is not free, because it keeps running there and the next
/// request on that session is refused as an invalid sequence, along with every one after it.
/// </summary>
public class MapiPendingKeepAliveTests
{
    /// <summary>A body that arrives in pieces, pausing between them the way keep-alives do.</summary>
    private sealed class DripStream(IReadOnlyList<(TimeSpan Delay, string Text)> pieces) : Stream
    {
        private int _next;
        private byte[] _pending = [];
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _pending.Length)
            {
                if (_next == pieces.Count)
                    return 0;

                var (delay, text) = pieces[_next++];
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                _pending = Encoding.ASCII.GetBytes(text);
                _offset = 0;
            }

            var take = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;

            return take;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Responding(params (TimeSpan Delay, string Text)[] pieces)
        => new() { Content = new StreamContent(new DripStream(pieces)) };

    [Fact]
    public async Task AReplyThatKeepsArrivingIsWaitedFor()
    {
        // Each piece lands inside the idle window, but together they take longer than it. A flat
        // timeout of the same size would have cut this off; silence is what should end it, not time.
        using var response = Responding(
            (TimeSpan.FromMilliseconds(120), "PROCESSING\r\n"),
            (TimeSpan.FromMilliseconds(120), "PENDING\r\n"),
            (TimeSpan.FromMilliseconds(120), "PENDING\r\n"),
            (TimeSpan.FromMilliseconds(120), "DONE\r\n"));

        var body = await MapiHttpTransport.ReadBodyAsync(response, TimeSpan.FromMilliseconds(250), cancellationToken: CancellationToken.None);

        Encoding.ASCII.GetString(body).Should().Be("PROCESSING\r\nPENDING\r\nPENDING\r\nDONE\r\n");
    }

    [Fact]
    public async Task AReplyThatGoesQuietIsGivenUpOn()
    {
        using var response = Responding(
            (TimeSpan.FromMilliseconds(20), "PROCESSING\r\n"),
            (TimeSpan.FromSeconds(30), "DONE\r\n"));

        var act = async () => await MapiHttpTransport.ReadBodyAsync(response, TimeSpan.FromMilliseconds(200), cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TheCallersOwnCancellationStillWins()
    {
        using var response = Responding((TimeSpan.FromSeconds(30), "DONE\r\n"));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await MapiHttpTransport.ReadBodyAsync(response, TimeSpan.FromMinutes(5), cancellationToken: cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("15000", 45)]   // three missed keep-alives
    [InlineData("30000", 90)]
    public void TheWindowFollowsWhatTheServerPromised(string pendingPeriod, int expectedSeconds)
        => MapiHttpTransport.IdleWindow(pendingPeriod, TimeSpan.FromSeconds(60))
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));

    [Theory]
    [InlineData("1")]
    [InlineData("100")]
    public void AVeryShortPromiseStillGetsAFloor(string pendingPeriod)
        => MapiHttpTransport.IdleWindow(pendingPeriod, TimeSpan.FromSeconds(60))
            .Should().Be(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    public void NoPromiseMeansTheCallersOwnPatience(string? pendingPeriod)
        => MapiHttpTransport.IdleWindow(pendingPeriod, TimeSpan.FromSeconds(75))
            .Should().Be(TimeSpan.FromSeconds(75));

    [Fact]
    public void TheShortLeashIsShorterThanTheOrdinaryOne()
    {
        // It exists to fail fast in front of somebody, so it has to be the smaller of the two -
        // otherwise marking a request interactive would change nothing.
        MapiHttpTransport.InteractiveReplyTimeout.Should().BeLessThan(TimeSpan.FromSeconds(60));
        MapiHttpTransport.InteractiveReplyTimeout.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void AShortReplyTimeoutDoesNotShortenHowLongAWorkingServerIsGiven()
    {
        // The real derivation, not a restatement of its inputs. The two clocks answer different
        // questions: fifteen seconds of a server saying NOTHING is enough to give up on; fifteen
        // seconds of silence from one that has started replying is not, and cutting that off is the
        // thing that poisons the session. If SilenceBudget is ever "simplified" to take the request
        // timeout as-is, this fails.
        var standard = TimeSpan.FromSeconds(60);

        MapiHttpTransport.SilenceBudget(MapiHttpTransport.InteractiveReplyTimeout, standard).Should().Be(standard);
    }

    [Fact]
    public void ACallerAskingForMorePatienceGetsIt()
    {
        // NotificationWait holds for minutes and is the only caller that raises the budget.
        var standard = TimeSpan.FromSeconds(60);

        MapiHttpTransport.SilenceBudget(TimeSpan.FromMinutes(7), standard).Should().Be(TimeSpan.FromMinutes(7));
        MapiHttpTransport.SilenceBudget(null, standard).Should().Be(standard);
    }
}
