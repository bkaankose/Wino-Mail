using System.Net;
using Wino.Mapi;
using Wino.Mapi.Transport;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// How a rejected request is classified, which is what decides whether the session it was sent on
/// gets reused.
///
/// This is the cheap half of a bug that cost a run of failed drafts: MS-OXCMAPIHTTP permits one
/// request at a time inside a Session Context, and when the server sees two it answers 15 and then
/// refuses everything else in that context. A client that keeps such a session keeps failing, so the
/// code has to be recognised rather than lumped in with ordinary transport errors.
/// </summary>
public class MapiTransportFaultTests
{
    private static MapiResponse Response(string? responseCode, HttpStatusCode status = HttpStatusCode.OK) => new()
    {
        HttpStatus = status,
        RequestType = "Execute",
        ResponseCodeHeader = responseCode,
        RawBody = [],
    };

    [Fact]
    public void ZeroIsTheOnlyCodeThatMeansAccepted()
    {
        Response("0").TransportSucceeded.Should().BeTrue();

        var act = () => Response("0").EnsureTransportSucceeded();
        act.Should().NotThrow();
    }

    [Fact]
    public void FifteenIsRecognisedAsAnInvalidSequence()
    {
        var act = () => Response("15").EnsureTransportSucceeded();

        act.Should().Throw<MapiTransportException>()
            .Which.IsInvalidSequence.Should().BeTrue();
    }

    [Fact]
    public void TheMessageSaysWhatFifteenMeans()
    {
        // The log line is where this gets diagnosed, and a bare number sends the reader to the spec.
        var act = () => Response("15").EnsureTransportSucceeded();

        act.Should().Throw<MapiTransportException>()
            .WithMessage("*InvalidSequence*");
    }

    [Fact]
    public void TenIsStillContextNotFoundAndNotAnInvalidSequence()
    {
        var act = () => Response("10").EnsureTransportSucceeded();

        var thrown = act.Should().Throw<MapiTransportException>().Which;

        thrown.IsContextNotFound.Should().BeTrue();
        thrown.IsInvalidSequence.Should().BeFalse();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("9")]
    [InlineData(null)]
    public void OtherRejectionsAreNeitherOfThoseThings(string? code)
    {
        var act = () => Response(code).EnsureTransportSucceeded();

        var thrown = act.Should().Throw<MapiTransportException>().Which;

        thrown.IsContextNotFound.Should().BeFalse();
        thrown.IsInvalidSequence.Should().BeFalse();
    }

    [Fact]
    public void AnUnauthorizedRejectionIsRecognisedWhateverTheCode()
    {
        var act = () => Response(null, HttpStatusCode.Unauthorized).EnsureTransportSucceeded();

        act.Should().Throw<MapiTransportException>()
            .Which.IsUnauthorized.Should().BeTrue();
    }
}
