using FluentAssertions;
using Wino.NotificationHost.Contracts;
using Xunit;

namespace Wino.NotificationHost.Tests;

public sealed class NotificationHostCodecTests
{
    [Fact]
    public void Request_RoundTripsEveryField()
    {
        var request = new NotificationHostRequest(
            new DateTimeOffset(2026, 9, 1, 12, 30, 0, TimeSpan.Zero),
            NotificationHostOperation.Show,
            NotificationHostApplication.Calendar,
            "<toast><visual><binding template=\"ToastGeneric\"><text>Zażółć</text></binding></visual></toast>",
            "event:42",
            "calendar:7");

        NotificationHostCodec.DecodeRequest(NotificationHostCodec.EncodeRequest(request))
            .Should().BeEquivalentTo(request);
    }

    [Fact]
    public void Activation_RoundTripsInputInStableOrder()
    {
        var activation = new NotificationHostActivation(
            DateTimeOffset.UtcNow,
            NotificationHostApplication.Calendar,
            "ToastCalendarActionKey=ToastCalendarSnoozeAction",
            new Dictionary<string, string>
            {
                ["snooze"] = "15",
                ["reply"] = "Hello"
            });

        var first = NotificationHostCodec.EncodeActivation(activation);
        var second = NotificationHostCodec.EncodeActivation(activation with
        {
            UserInput = activation.UserInput.Reverse().ToDictionary()
        });

        first.Should().Equal(second);
        NotificationHostCodec.DecodeActivation(first).Should().BeEquivalentTo(activation);
    }

    [Fact]
    public void DecodeRequest_RejectsTruncatedAndTrailingData()
    {
        var request = new NotificationHostRequest(
            DateTimeOffset.UtcNow,
            NotificationHostOperation.RemoveByTag,
            NotificationHostApplication.Mail,
            null,
            "message:42",
            null);
        var encoded = NotificationHostCodec.EncodeRequest(request);

        var truncated = () => NotificationHostCodec.DecodeRequest(encoded[..^1]);
        var withTrailingData = () => NotificationHostCodec.DecodeRequest([.. encoded, 0x7F]);

        truncated.Should().Throw<EndOfStreamException>();
        withTrailingData.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void EncodeRequest_RejectsOversizedPayload()
    {
        var request = new NotificationHostRequest(
            DateTimeOffset.UtcNow,
            NotificationHostOperation.Show,
            NotificationHostApplication.Mail,
            new string('x', 256 * 1024 + 1),
            null,
            null);

        var action = () => NotificationHostCodec.EncodeRequest(request);

        action.Should().Throw<InvalidDataException>();
    }
}
