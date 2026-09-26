using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;
using Xunit;

namespace Wino.Core.Tests;

public class AccountCapabilitySummaryTests
{
    [Fact]
    public void Build_ListsEveryModeThatIsOn()
    {
        var account = new MailAccount
        {
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = true,
            IsContactAccessEnabled = true,
            IsTaskAccessGranted = true
        };

        AccountCapabilitySummary.Build(account).Should().Be(
            $"{Translator.AccountCapability_Mail} + {Translator.AccountCapability_Calendar} + {Translator.AccountCapability_People} + {Translator.AccountCapability_ToDo}");
    }

    [Fact]
    public void Build_CountsALocalModeAsOn()
    {
        var account = new MailAccount
        {
            IsMailAccessGranted = false,
            IsCalendarAccessGranted = false,
            IsCalendarAccessEnabled = true,
            IsContactAccessEnabled = false,
            IsTaskAccessEnabled = false
        };

        AccountCapabilitySummary.Build(account).Should().Be(Translator.AccountCapability_Calendar);
    }

    [Fact]
    public void Build_SaysSoWhenNothingIsOn()
    {
        var account = new MailAccount
        {
            IsMailAccessGranted = false,
            IsContactAccessEnabled = false
        };

        AccountCapabilitySummary.Build(account).Should().Be(Translator.AccountCapability_None);
    }
}
