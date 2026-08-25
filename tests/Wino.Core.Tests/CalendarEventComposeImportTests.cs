using System.Globalization;
using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarEventComposeImportTests
{
    [Fact]
    public async Task ImportedArguments_PopulateComposerWithoutSaving()
    {
        var accounts = new Mock<IAccountService>();
        accounts.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var calendarService = new Mock<ICalendarService>();
        calendarService.Setup(service => service.GetPredefinedReminderMinutes()).Returns([60, 15, 5, 1]);
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(service => service.GetCurrentCalendarSettings()).Returns(CreateSettings());
        preferences.SetupGet(service => service.DefaultReminderDurationInSeconds).Returns(15 * 60);
        var delegator = new Mock<IWinoRequestDelegator>();
        var viewModel = new CalendarEventComposePageViewModel(
            accounts.Object,
            calendarService.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IContactService>(),
            preferences.Object,
            Mock.Of<IUnderlyingThemeService>(),
            delegator.Object);
        var args = new CalendarEventComposeNavigationArgs
        {
            Title = "Imported meeting",
            Location = "Room 4",
            StartDate = new DateTime(2026, 8, 25, 9, 30, 0),
            EndDate = new DateTime(2026, 8, 25, 10, 45, 0),
            ShowAs = CalendarItemShowAs.Tentative,
            Attendees =
            [
                new("Alex", "alex@example.com"),
                new("Duplicate", "ALEX@example.com"),
                new("Sam", "sam@example.com")
            ],
            ReminderMinutesBeforeStart = 17,
            Recurrence = new CalendarEventRecurrenceDraft
            {
                Frequency = CalendarItemRecurrenceFrequency.Daily,
                Interval = 2,
                Weekdays = [DayOfWeek.Monday, DayOfWeek.Wednesday],
                EndDate = new DateTime(2026, 9, 30)
            }
        };

        viewModel.OnNavigatedTo(NavigationMode.New, args);

        await WaitForAsync(() => viewModel.Title == "Imported meeting");

        viewModel.Location.Should().Be("Room 4");
        viewModel.StartDate.Date.Should().Be(new DateTime(2026, 8, 25));
        viewModel.StartTime.Should().Be(new TimeSpan(9, 30, 0));
        viewModel.EndTime.Should().Be(new TimeSpan(10, 45, 0));
        viewModel.SelectedShowAsOption.ShowAs.Should().Be(CalendarItemShowAs.Tentative);
        viewModel.Attendees.Select(attendee => attendee.Email).Should().Equal("alex@example.com", "sam@example.com");
        viewModel.SelectedReminderOption.Minutes.Should().Be(17);
        viewModel.SelectedReminderOption.IsCustom.Should().BeTrue();
        viewModel.IsRecurring.Should().BeTrue();
        viewModel.SelectedRecurrenceFrequencyOption.Frequency.Should().Be(CalendarItemRecurrenceFrequency.Daily);
        viewModel.SelectedRecurrenceInterval.Should().Be(2);
        viewModel.WeekdayOptions.Where(day => day.IsSelected).Select(day => day.DayOfWeek)
            .Should().BeEquivalentTo([DayOfWeek.Monday, DayOfWeek.Wednesday]);
        viewModel.RecurrenceEndDate!.Value.Date.Should().Be(new DateTime(2026, 9, 30));
        delegator.Verify(service => service.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()), Times.Never);
    }

    private static CalendarSettings CreateSettings()
        => new(
            DayOfWeek.Monday,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            true,
            DayOfWeek.Monday,
            DayOfWeek.Friday,
            TimeSpan.FromHours(9),
            TimeSpan.FromHours(17),
            60,
            DayHeaderDisplayType.TwentyFourHour,
            CultureInfo.InvariantCulture);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < timeout)
            await Task.Delay(10);

        predicate().Should().BeTrue();
    }
}
