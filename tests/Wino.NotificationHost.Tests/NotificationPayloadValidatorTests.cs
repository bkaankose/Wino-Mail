using FluentAssertions;
using Wino.NotificationHost.Contracts;
using Xunit;

namespace Wino.NotificationHost.Tests;

public sealed class NotificationPayloadValidatorTests
{
    [Fact]
    public void Validate_AcceptsPackageLocalImagesAndAudio()
    {
        const string payload = """
            <toast>
              <visual><binding template="ToastGeneric"><image src="ms-appdata:///local/contacts/picture.jpg" /></binding></visual>
              <audio src="ms-winsoundevent:Notification.Mail" />
            </toast>
            """;

        var action = () => NotificationPayloadValidator.Validate(payload);

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData("<toast><visual><binding template=\"ToastGeneric\"><image src=\"https://example.com/a.png\" /></binding></visual></toast>")]
    [InlineData("<toast><actions><action content=\"Open\" arguments=\"wino://mail\" activationType=\"protocol\" /></actions></toast>")]
    [InlineData("<!DOCTYPE toast [<!ENTITY xxe SYSTEM \"file:///c:/secret\">]><toast><text>&xxe;</text></toast>")]
    public void Validate_RejectsExternalOrActiveContent(string payload)
    {
        var action = () => NotificationPayloadValidator.Validate(payload);

        action.Should().Throw<Exception>();
    }
}
