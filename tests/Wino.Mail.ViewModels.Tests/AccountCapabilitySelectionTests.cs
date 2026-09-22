using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class AccountCapabilitySelectionTests
{
    [Fact]
    public void TurningCapabilityOffAndOn_RestoresItsPreviousMode()
    {
        var selection = new AccountCapabilitySelection();
        selection.CalendarMode = AccountCapabilityMode.Local;

        selection.IsCalendarEnabled = false;
        selection.CalendarMode.Should().Be(AccountCapabilityMode.Off);

        selection.IsCalendarEnabled = true;
        selection.CalendarMode.Should().Be(AccountCapabilityMode.Local);
    }

    [Fact]
    public void TurningCapabilityOn_FallsBackToLocalWhenProviderIsUnavailable()
    {
        var selection = new AccountCapabilitySelection();
        selection.IsContactEnabled = false;

        selection.ConfigureProvider(true, false, false, "CalDAV", "CardDAV", string.Empty);
        selection.IsContactEnabled = true;

        selection.ContactMode.Should().Be(AccountCapabilityMode.Local);
    }

    [Fact]
    public void ConfigureProvider_MovesUnavailableProviderModesToLocal()
    {
        var selection = new AccountCapabilitySelection
        {
            CalendarMode = AccountCapabilityMode.Provider,
            ContactMode = AccountCapabilityMode.Provider,
            TaskMode = AccountCapabilityMode.Provider
        };

        selection.ConfigureProvider(false, false, false, string.Empty, string.Empty, string.Empty);

        selection.CalendarMode.Should().Be(AccountCapabilityMode.Local);
        selection.ContactMode.Should().Be(AccountCapabilityMode.Local);
        selection.TaskMode.Should().Be(AccountCapabilityMode.Local);
        selection.IsCalendarLocalOnlyNoteVisible.Should().BeTrue();
        selection.IsCalendarModeChoiceVisible.Should().BeFalse();
    }

    [Fact]
    public void RadioOptions_IgnoreTheFalseWriteOfTheOptionTheyLeave()
    {
        var selection = new AccountCapabilitySelection { CalendarMode = AccountCapabilityMode.Provider };

        selection.IsCalendarLocalSelected = true;
        selection.IsCalendarProviderSelected = false;

        selection.CalendarMode.Should().Be(AccountCapabilityMode.Local);
    }

    [Theory]
    [InlineData(AccountCapabilityMode.Off, AccountCapabilityMode.Local, AccountCapabilityMode.Local, AccountCapabilityMode.Local, false)]
    [InlineData(AccountCapabilityMode.Off, AccountCapabilityMode.Off, AccountCapabilityMode.Off, AccountCapabilityMode.Local, false)]
    [InlineData(AccountCapabilityMode.Off, AccountCapabilityMode.Provider, AccountCapabilityMode.Off, AccountCapabilityMode.Off, true)]
    [InlineData(AccountCapabilityMode.Off, AccountCapabilityMode.Off, AccountCapabilityMode.Provider, AccountCapabilityMode.Off, true)]
    [InlineData(AccountCapabilityMode.Provider, AccountCapabilityMode.Off, AccountCapabilityMode.Off, AccountCapabilityMode.Off, true)]
    public void RequiresRemoteService_IsTrueOnlyWhenACapabilityUsesAServer(
        AccountCapabilityMode mail,
        AccountCapabilityMode calendar,
        AccountCapabilityMode contacts,
        AccountCapabilityMode tasks,
        bool expected)
    {
        var selection = new AccountCapabilitySelection
        {
            MailMode = mail,
            CalendarMode = calendar,
            ContactMode = contacts,
            TaskMode = tasks
        };

        selection.RequiresRemoteService.Should().Be(expected);
        selection.HasAnyCapability.Should().BeTrue();
    }

    [Fact]
    public void EverythingOff_IsAMissingSelection()
    {
        var selection = new AccountCapabilitySelection
        {
            MailMode = AccountCapabilityMode.Off,
            CalendarMode = AccountCapabilityMode.Off,
            ContactMode = AccountCapabilityMode.Off,
            TaskMode = AccountCapabilityMode.Off
        };

        selection.IsSelectionMissing.Should().BeTrue();
        selection.RequiresRemoteService.Should().BeFalse();
    }
}
