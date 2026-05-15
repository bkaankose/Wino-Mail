using FluentAssertions;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class WelcomeWizardContextTests
{
    [Fact]
    public void NewWizardContext_DefaultsToMailOnly()
    {
        var context = new WelcomeWizardContext();

        context.IsMailAccessEnabled.Should().BeTrue();
        context.IsCalendarAccessEnabled.Should().BeFalse();
    }

    [Fact]
    public void Reset_DefaultsToMailOnly()
    {
        var context = new WelcomeWizardContext
        {
            IsMailAccessEnabled = false,
            IsCalendarAccessEnabled = true
        };

        context.Reset();

        context.IsMailAccessEnabled.Should().BeTrue();
        context.IsCalendarAccessEnabled.Should().BeFalse();
    }
}
