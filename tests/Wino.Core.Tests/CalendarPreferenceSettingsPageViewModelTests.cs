using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarPreferenceSettingsPageViewModelTests
{
    [Fact]
    public async Task Initialization_LoadsEligibleAccountsInAccountOrderAndRepairsInvalidStartup()
    {
        var later = CreateAccount("Later", 2, supportsCalendar: true);
        var first = CreateAccount("First", 1, supportsCalendar: true);
        var empty = CreateAccount("Empty", 0, supportsCalendar: true);
        var unsupported = CreateAccount("Unsupported", 3, supportsCalendar: false);
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([later, unsupported, first, empty]);
        var calendarService = new Mock<ICalendarService>();
        calendarService.Setup(service => service.GetAccountCalendarsAsync(first.Id)).ReturnsAsync([CreateCalendar(first, "First calendar")]);
        calendarService.Setup(service => service.GetAccountCalendarsAsync(later.Id)).ReturnsAsync([CreateCalendar(later, "Later calendar")]);
        calendarService.Setup(service => service.GetAccountCalendarsAsync(empty.Id)).ReturnsAsync([]);
        calendarService.Setup(service => service.GetAccountCalendarsAsync(unsupported.Id)).ReturnsAsync([]);
        var preferences = CreatePreferences(grouped: false, startupAccountId: Guid.NewGuid());

        var viewModel = new CalendarPreferenceSettingsPageViewModel(preferences.Object, calendarService.Object, accountService.Object);
        await viewModel.InitializationTask;

        viewModel.CalendarStartupAccounts.Should().ContainInOrder(first, later);
        viewModel.AvailableNewEventCalendars.Select(calendar => calendar.Account).Should().ContainInOrder(first, later);
        viewModel.SelectedCalendarStartupAccount.Should().Be(first);
        preferences.Object.CalendarStartupAccountId.Should().Be(first.Id);
        viewModel.ShouldShowCalendarStartupAccount.Should().BeTrue();
        calendarService.Verify(service => service.GetAccountCalendarsAsync(unsupported.Id), Times.Once);
    }

    [Fact]
    public async Task SelectionAndGroupingChanges_WritePreferencesAndUpdateVisibility()
    {
        var first = CreateAccount("First", 1, supportsCalendar: true);
        var second = CreateAccount("Second", 2, supportsCalendar: true);
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([first, second]);
        var calendarService = new Mock<ICalendarService>();
        calendarService.Setup(service => service.GetAccountCalendarsAsync(first.Id)).ReturnsAsync([CreateCalendar(first, "First calendar")]);
        calendarService.Setup(service => service.GetAccountCalendarsAsync(second.Id)).ReturnsAsync([CreateCalendar(second, "Second calendar")]);
        var preferences = CreatePreferences(grouped: false, startupAccountId: first.Id);
        var viewModel = new CalendarPreferenceSettingsPageViewModel(preferences.Object, calendarService.Object, accountService.Object);
        await viewModel.InitializationTask;

        viewModel.SelectedCalendarStartupAccount = second;
        viewModel.IsCalendarAccountsGrouped = true;

        preferences.Object.CalendarStartupAccountId.Should().Be(second.Id);
        preferences.Object.IsCalendarAccountsGrouped.Should().BeTrue();
        viewModel.ShouldShowCalendarStartupAccount.Should().BeFalse();
    }

    [Fact]
    public async Task Initialization_WithNoEligibleAccounts_ClearsStartupPreference()
    {
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var preferences = CreatePreferences(grouped: false, startupAccountId: Guid.NewGuid());

        var viewModel = new CalendarPreferenceSettingsPageViewModel(
            preferences.Object,
            Mock.Of<ICalendarService>(),
            accountService.Object);
        await viewModel.InitializationTask;

        viewModel.CalendarStartupAccounts.Should().BeEmpty();
        viewModel.SelectedCalendarStartupAccount.Should().BeNull();
        preferences.Object.CalendarStartupAccountId.Should().BeNull();
    }

    private static Mock<IPreferencesService> CreatePreferences(bool grouped, Guid? startupAccountId)
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(service => service.IsCalendarAccountsGrouped, grouped);
        preferences.SetupProperty(service => service.CalendarStartupAccountId, startupAccountId);
        preferences.SetupProperty(service => service.NewEventButtonBehavior, NewEventButtonBehavior.AskEachTime);
        preferences.SetupProperty(service => service.DefaultNewEventCalendarId, null);
        return preferences;
    }

    private static MailAccount CreateAccount(string name, int order, bool supportsCalendar)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Address = $"{name.ToLowerInvariant()}@example.com",
            Order = order,
            IsCalendarAccessGranted = supportsCalendar
        };

    private static AccountCalendar CreateCalendar(MailAccount account, string name)
        => new()
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Name = name,
            IsExtended = true
        };
}
