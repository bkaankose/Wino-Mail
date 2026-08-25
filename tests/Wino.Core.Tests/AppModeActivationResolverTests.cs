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
    [InlineData("--wino-todo", WinoApplicationMode.Mail, WinoApplicationMode.Tasks)]
    [InlineData("ToDoApp", WinoApplicationMode.Mail, WinoApplicationMode.Tasks)]
    [InlineData("--mode=tasks", WinoApplicationMode.Mail, WinoApplicationMode.Tasks)]
    [InlineData("App", WinoApplicationMode.Calendar, WinoApplicationMode.Mail)]
    public void Resolve_PrefersKnownMailCalendarSignals(string source, WinoApplicationMode defaultMode, WinoApplicationMode expectedMode)
    {
        var resolvedMode = AppModeActivationResolver.Resolve(source, null, null, defaultMode);

        resolvedMode.Should().Be(expectedMode);
    }

    [Theory]
    [InlineData("--wino-people")]
    [InlineData("--wino-contacts")]
    [InlineData("--mode=people")]
    [InlineData("--mode=contacts")]
    [InlineData("PeopleApp")]
    [InlineData("ContactsApp")]
    public void Resolve_AcceptsPeopleBrandingAndLegacyContactsSignals(string source)
    {
        AppModeActivationResolver.Resolve(source, null, null, WinoApplicationMode.Mail)
            .Should().Be(WinoApplicationMode.Contacts);
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
