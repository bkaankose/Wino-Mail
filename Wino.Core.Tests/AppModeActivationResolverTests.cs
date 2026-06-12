using FluentAssertions;
using Wino.Core.Activation;
using Wino.Core.Domain.Enums;
using Xunit;

namespace Wino.Core.Tests;

public class AppModeActivationResolverTests
{
    [Theory]
    [InlineData("--wino-mail", WinoApplicationMode.Calendar, WinoApplicationMode.Mail)]
    [InlineData("--wino-calendar", WinoApplicationMode.Mail, WinoApplicationMode.Calendar)]
    [InlineData("--mode=mail", WinoApplicationMode.Calendar, WinoApplicationMode.Mail)]
    [InlineData("--mode=calendar", WinoApplicationMode.Mail, WinoApplicationMode.Calendar)]
    [InlineData("CalendarApp", WinoApplicationMode.Mail, WinoApplicationMode.Calendar)]
    [InlineData("App", WinoApplicationMode.Calendar, WinoApplicationMode.Mail)]
    public void Resolve_PrefersKnownMailCalendarSignals(string source, WinoApplicationMode defaultMode, WinoApplicationMode expectedMode)
    {
        var resolvedMode = AppModeActivationResolver.Resolve(source, null, null, defaultMode);

        resolvedMode.Should().Be(expectedMode);
    }

    [Fact]
    public void Resolve_ToggleDefaultArgumentFlipsBetweenMailAndCalendar()
    {
        AppModeActivationResolver.Resolve("--mode=toggle-default", null, null, WinoApplicationMode.Mail)
            .Should().Be(WinoApplicationMode.Calendar);

        AppModeActivationResolver.Resolve("--mode=toggle-default", null, null, WinoApplicationMode.Calendar)
            .Should().Be(WinoApplicationMode.Mail);
    }
}
