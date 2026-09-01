using FluentAssertions;
using Wino.NotificationHost.Contracts;
using Xunit;

namespace Wino.NotificationHost.Tests;

public sealed class NotificationHostIdentityTests
{
    [Theory]
    [InlineData("family!MailNotificationHost", NotificationHostApplication.Mail)]
    [InlineData("family!CalendarNotificationHost", NotificationHostApplication.Calendar)]
    [InlineData("family!PeopleNotificationHost", NotificationHostApplication.People)]
    [InlineData("family!ToDoNotificationHost", NotificationHostApplication.Tasks)]
    public void ResolveFromAppUserModelId_MapsKnownHost(string value, NotificationHostApplication expected)
    {
        NotificationHostApplicationIds.TryResolveFromAppUserModelId(value, out var actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public void ForwardedActivationArguments_ParseAlongsideManifestArguments()
    {
        var activationId = Guid.NewGuid();
        var arguments = $"--wino-mail {NotificationHostLaunchArguments.CreateForwardedActivation(activationId)}";

        NotificationHostLaunchArguments.TryParseForwardedActivation(arguments, out var parsed).Should().BeTrue();
        parsed.Should().Be(activationId);
    }
}
