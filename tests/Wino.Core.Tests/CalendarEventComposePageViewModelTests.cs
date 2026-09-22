using System.Globalization;
using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarEventComposePageViewModelTests
{
    private readonly Mock<IWinoRequestDelegator> _delegator = new();
    private readonly Mock<IMailDialogService> _dialogService = new();
    private CalendarOperationPreparationRequest? _capturedRequest;

    public CalendarEventComposePageViewModelTests()
    {
        _delegator
            .Setup(service => service.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()))
            .Callback<CalendarOperationPreparationRequest>(request => _capturedRequest = request)
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task TimedEvent_CanEndOnALaterDay()
    {
        var viewModel = CreateViewModel();
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 18, 30, 0), new DateTime(2026, 9, 27, 21, 0, 0));

        viewModel.IsDateRangeValid.Should().BeTrue();
        viewModel.HasEndDayOffset.Should().BeTrue();
        viewModel.EndDayOffset.Should().Be(2);
        viewModel.CreateCommand.CanExecute(null).Should().BeTrue();

        await viewModel.CreateCommand.ExecuteAsync(null);

        var result = _capturedRequest!.ComposeResult;
        result.StartDate.Should().Be(new DateTime(2026, 9, 25, 18, 30, 0));
        result.EndDate.Should().Be(new DateTime(2026, 9, 27, 21, 0, 0));
        result.IsAllDay.Should().BeFalse();
    }

    [Fact]
    public void EndBeforeStart_DisablesSaveAndShowsError()
    {
        var viewModel = CreateViewModel();
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 18, 30, 0), new DateTime(2026, 9, 25, 19, 0, 0));

        viewModel.EndDate = new DateTimeOffset(new DateTime(2026, 9, 24));

        viewModel.IsDateRangeValid.Should().BeFalse();
        viewModel.HasEndDayOffset.Should().BeFalse();
        viewModel.DateRangeErrorText.Should().Be(Translator.CalendarEventCompose_ValidationInvalidTimeRange);
        viewModel.CreateCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void MovingTheStart_KeepsTheDuration()
    {
        var viewModel = CreateViewModel();
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 22, 0, 0), new DateTime(2026, 9, 25, 23, 30, 0));

        viewModel.StartTime = new TimeSpan(23, 0, 0);

        viewModel.EndDate.Date.Should().Be(new DateTime(2026, 9, 26));
        viewModel.EndTime.Should().Be(new TimeSpan(0, 30, 0));

        viewModel.StartDate = new DateTimeOffset(new DateTime(2026, 9, 28));

        viewModel.EndDate.Date.Should().Be(new DateTime(2026, 9, 29));
        viewModel.EndTime.Should().Be(new TimeSpan(0, 30, 0));
    }

    [Fact]
    public async Task AllDayEvent_ShowsInclusiveEndAndSendsExclusiveEnd()
    {
        var viewModel = CreateViewModel();
        viewModel.OnNavigatedTo(NavigationMode.New, new CalendarEventComposeNavigationArgs
        {
            Title = "Kraków",
            IsAllDay = true,
            StartDate = new DateTime(2026, 9, 25),
            EndDate = new DateTime(2026, 9, 28)
        });

        await WaitForAsync(() => viewModel.Title == "Kraków");
        viewModel.SelectedCalendar = CreateCalendar(MailProviderType.Gmail);

        viewModel.EndDate.Date.Should().Be(new DateTime(2026, 9, 27));
        viewModel.DurationText.Should().Be(string.Format(Translator.CalendarEventCompose_DurationDays, 3));

        await viewModel.CreateCommand.ExecuteAsync(null);

        var result = _capturedRequest!.ComposeResult;
        result.IsAllDay.Should().BeTrue();
        result.StartDate.Should().Be(new DateTime(2026, 9, 25));
        result.EndDate.Should().Be(new DateTime(2026, 9, 28));
    }

    [Fact]
    public void SingleDayAllDayEvent_IsValid()
    {
        var viewModel = CreateViewModel();
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 9, 0, 0), new DateTime(2026, 9, 25, 10, 0, 0));

        viewModel.IsAllDay = true;

        viewModel.EndDate.Date.Should().Be(new DateTime(2026, 9, 25));
        viewModel.IsDateRangeValid.Should().BeTrue();
        viewModel.HasEndDayOffset.Should().BeFalse();
    }

    [Fact]
    public void WeekdaysPreset_BuildsWeeklyRuleWithWorkDays()
    {
        var viewModel = CreateViewModel();
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 9, 0, 0), new DateTime(2026, 9, 25, 10, 0, 0));

        viewModel.SelectedRepeatOption = viewModel.RepeatOptions.Single(option => option.Kind == CalendarComposeRepeatKind.Weekdays);

        viewModel.IsRecurring.Should().BeTrue();
        viewModel.IsCustomRecurrence.Should().BeFalse();
        BuildRule(viewModel).Should().Be("RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,TU,WE,TH,FR");
    }

    [Fact]
    public void SwitchingToCustom_StartsFromThePreviousPreset()
    {
        var viewModel = CreateViewModel();
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 9, 0, 0), new DateTime(2026, 9, 25, 10, 0, 0));

        viewModel.SelectedRepeatOption = viewModel.RepeatOptions.Single(option => option.Kind == CalendarComposeRepeatKind.Weekly);
        viewModel.SelectedRepeatOption = viewModel.RepeatOptions.Single(option => option.Kind == CalendarComposeRepeatKind.Custom);

        viewModel.IsWeeklyCustomRecurrence.Should().BeTrue();
        viewModel.WeekdayOptions.Where(day => day.IsSelected).Select(day => day.DayOfWeek).Should().Equal(DayOfWeek.Friday);

        viewModel.WeekdayOptions.Single(day => day.DayOfWeek == DayOfWeek.Tuesday).IsSelected = true;
        viewModel.SelectedRecurrenceInterval = 2;

        BuildRule(viewModel).Should().Be("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,FR");
    }

    [Fact]
    public async Task ImportedDailyRuleWithWorkDays_BecomesWeekdaysPreset()
    {
        var viewModel = CreateViewModel();
        viewModel.OnNavigatedTo(NavigationMode.New, new CalendarEventComposeNavigationArgs
        {
            Title = "Stand-up",
            StartDate = new DateTime(2026, 9, 21, 9, 0, 0),
            EndDate = new DateTime(2026, 9, 21, 9, 15, 0),
            Recurrence = new CalendarEventRecurrenceDraft
            {
                Frequency = CalendarItemRecurrenceFrequency.Daily,
                Interval = 1,
                Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]
            }
        });

        await WaitForAsync(() => viewModel.Title == "Stand-up");

        viewModel.SelectedRepeatOption.Kind.Should().Be(CalendarComposeRepeatKind.Weekdays);
    }

    [Fact]
    public async Task TimedRecurrenceEnd_IsSentInUtc()
    {
        var viewModel = CreateViewModel();
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 9, 0, 0), new DateTime(2026, 9, 25, 10, 0, 0));
        viewModel.SelectedTimeZoneOption = viewModel.TimeZoneOptions.Single(option => option.Id == TimeZoneInfo.Utc.Id);
        viewModel.SelectedRepeatOption = viewModel.RepeatOptions.Single(option => option.Kind == CalendarComposeRepeatKind.Daily);
        viewModel.RecurrenceEndDate = new DateTimeOffset(new DateTime(2026, 12, 18));

        await viewModel.CreateCommand.ExecuteAsync(null);

        _capturedRequest!.ComposeResult.Recurrence.Should().Be("RRULE:FREQ=DAILY;INTERVAL=1;UNTIL=20261218T235959Z");
        _capturedRequest.ComposeResult.TimeZoneId.Should().Be(TimeZoneInfo.Utc.Id);
    }

    [Fact]
    public async Task RecurrenceEndBeforeStart_ShowsValidationInsteadOfThrowing()
    {
        var viewModel = CreateViewModel();
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 9, 0, 0), new DateTime(2026, 9, 25, 10, 0, 0));
        viewModel.SelectedRepeatOption = viewModel.RepeatOptions.Single(option => option.Kind == CalendarComposeRepeatKind.Daily);
        viewModel.RecurrenceEndDate = new DateTimeOffset(new DateTime(2026, 9, 1));

        await viewModel.CreateCommand.ExecuteAsync(null);

        _capturedRequest.Should().BeNull();
        _dialogService.Verify(service => service.InfoBarMessage(
            Translator.CalendarEventCompose_ValidationTitle,
            Translator.CalendarEventCompose_ValidationInvalidRecurrenceEnd,
            InfoBarMessageType.Warning), Times.Once);
    }

    [Fact]
    public async Task PrivateOnlineMeetingAndOptionalAttendee_AreSent()
    {
        var viewModel = CreateViewModel(MailProviderType.Outlook);
        SetTimedRange(viewModel, new DateTime(2026, 9, 25, 9, 0, 0), new DateTime(2026, 9, 25, 10, 0, 0));
        var attendee = new CalendarComposeAttendeeViewModel("Sam", "sam@example.com");
        viewModel.AddAttendee(attendee);

        viewModel.IsPrivate = true;
        viewModel.IsOnlineMeeting = true;
        viewModel.ToggleAttendeeOptionalCommand.Execute(attendee);

        viewModel.SaveButtonText.Should().Be(Translator.Buttons_Send);
        viewModel.OnlineMeetingProviderText.Should().Be(Translator.CalendarEventCompose_OnlineMeetingTeams);

        await viewModel.CreateCommand.ExecuteAsync(null);

        var result = _capturedRequest!.ComposeResult;
        result.Visibility.Should().Be(CalendarItemVisibility.Private);
        result.IsOnlineMeeting.Should().BeTrue();
        result.Attendees.Should().ContainSingle(item => item.Email == "sam@example.com" && item.IsOptionalAttendee);
    }

    [Fact]
    public void OnlineMeeting_IsClearedForCalendarsWithoutConferencing()
    {
        var viewModel = CreateViewModel(MailProviderType.Gmail);
        viewModel.IsOnlineMeeting = true;

        viewModel.SelectedCalendar = CreateCalendar(MailProviderType.IMAP4);

        viewModel.CanAddOnlineMeeting.Should().BeFalse();
        viewModel.IsOnlineMeeting.Should().BeFalse();
    }

    [Fact]
    public void SummaryFormatter_DescribesMultiDayEvents()
    {
        var summary = CalendarRecurrenceSummaryFormatter.BuildSummary(
            false,
            new DateTimeOffset(new DateTime(2026, 9, 25, 18, 30, 0)),
            new DateTimeOffset(new DateTime(2026, 9, 27, 21, 0, 0)),
            false,
            CreateSettings(),
            1,
            CalendarItemRecurrenceFrequency.Weekly,
            [],
            null);

        summary.Should().Be(string.Format(
            CultureInfo.InvariantCulture,
            Translator.CalendarEventCompose_MultiDayOccurrenceSummary,
            "Friday 2026-09-25 18:30",
            "Sunday 2026-09-27 21:00"));
    }

    private CalendarEventComposePageViewModel CreateViewModel(MailProviderType providerType = MailProviderType.Gmail)
    {
        var accounts = new Mock<IAccountService>();
        accounts.Setup(service => service.GetAccountsAsync()).ReturnsAsync([]);
        var calendarService = new Mock<ICalendarService>();
        calendarService.Setup(service => service.GetPredefinedReminderMinutes()).Returns([60, 15, 5, 1]);
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(service => service.GetCurrentCalendarSettings()).Returns(CreateSettings());
        preferences.SetupGet(service => service.DefaultReminderDurationInSeconds).Returns(15 * 60);

        var viewModel = new CalendarEventComposePageViewModel(
            accounts.Object,
            calendarService.Object,
            Mock.Of<INavigationService>(),
            _dialogService.Object,
            Mock.Of<IContactService>(),
            preferences.Object,
            Mock.Of<IUnderlyingThemeService>(),
            _delegator.Object)
        {
            Title = "Event"
        };

        viewModel.SelectedCalendar = CreateCalendar(providerType);
        return viewModel;
    }

    private static AccountCalendarViewModel CreateCalendar(MailProviderType providerType)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            Address = "me@example.com",
            SenderName = "Me",
            ProviderType = providerType
        };

        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Name = "Calendar",
            IsPrimary = true,
            BackgroundColorHex = "#3366ff"
        };

        return new AccountCalendarViewModel(account, calendar);
    }

    private static void SetTimedRange(CalendarEventComposePageViewModel viewModel, DateTime start, DateTime end)
    {
        viewModel.IsAllDay = false;

        // The end is set last, so moving the start cannot shift it afterwards.
        viewModel.StartDate = new DateTimeOffset(start.Date);
        viewModel.StartTime = start.TimeOfDay;
        viewModel.EndDate = new DateTimeOffset(end.Date);
        viewModel.EndTime = end.TimeOfDay;
    }

    private string BuildRule(CalendarEventComposePageViewModel viewModel)
    {
        viewModel.CreateCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return _capturedRequest!.ComposeResult.Recurrence;
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
