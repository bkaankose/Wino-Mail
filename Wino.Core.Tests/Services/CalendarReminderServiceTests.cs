using FluentAssertions;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class CalendarReminderServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private CalendarService _calendarService = null!;
    private AccountCalendar _testCalendar = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _calendarService = new CalendarService(_databaseService);

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Reminder Test",
            Address = "reminder@test.local",
            SenderName = "Reminder Test",
            IsCalendarAccessGranted = true
        };

        await _databaseService.Connection.InsertAsync(account, typeof(MailAccount));

        _testCalendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Name = "Test Calendar",
            TimeZone = "UTC",
            IsPrimary = true,
            BackgroundColorHex = "#0A84FF",
            TextColorHex = "#FFFFFF"
        };

        await _calendarService.InsertAccountCalendarAsync(_testCalendar);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task CheckAndNotifyAsync_WhenReminderFallsWithinWindow_ReturnsDueReminder()
    {
        var nowLocal = new DateTime(2026, 1, 1, 10, 0, 0);
        var lastCheckLocal = nowLocal.AddSeconds(-30);

        var calendarItem = await CreateCalendarItemWithReminderAsync(
            startDate: nowLocal.AddMinutes(5),
            reminderDurationInSeconds: 5 * 60,
            reminderType: CalendarItemReminderType.Popup);

        HashSet<string> sentReminderKeys = [];

        var due = await _calendarService.CheckAndNotifyAsync(lastCheckLocal, nowLocal, sentReminderKeys);

        due.Should().HaveCount(1);
        due[0].CalendarItem.Id.Should().Be(calendarItem.Id);
        due[0].ReminderDurationInSeconds.Should().Be(5 * 60);
        due[0].ReminderKey.Should().StartWith($"{calendarItem.Id:N}:{5 * 60}:");
        sentReminderKeys.Should().ContainSingle(k => k.StartsWith($"{calendarItem.Id:N}:{5 * 60}:"));
    }

    [Fact]
    public async Task CheckAndNotifyAsync_WhenReminderIsOutsideWindow_ReturnsEmpty()
    {
        var nowLocal = new DateTime(2026, 1, 1, 10, 0, 0);
        var lastCheckLocal = nowLocal.AddSeconds(-30);

        await CreateCalendarItemWithReminderAsync(
            startDate: nowLocal.AddMinutes(20),
            reminderDurationInSeconds: 5 * 60,
            reminderType: CalendarItemReminderType.Popup);

        HashSet<string> sentReminderKeys = [];

        var due = await _calendarService.CheckAndNotifyAsync(lastCheckLocal, nowLocal, sentReminderKeys);

        due.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAndNotifyAsync_WhenReminderAlreadySent_DoesNotReturnDuplicate()
    {
        var nowLocal = new DateTime(2026, 1, 1, 10, 0, 0);
        var lastCheckLocal = nowLocal.AddSeconds(-30);

        var calendarItem = await CreateCalendarItemWithReminderAsync(
            startDate: nowLocal.AddMinutes(5),
            reminderDurationInSeconds: 5 * 60,
            reminderType: CalendarItemReminderType.Popup);

        HashSet<string> sentReminderKeys = [];

        var firstRun = await _calendarService.CheckAndNotifyAsync(lastCheckLocal, nowLocal, sentReminderKeys);
        var secondRun = await _calendarService.CheckAndNotifyAsync(lastCheckLocal, nowLocal, sentReminderKeys);

        firstRun.Should().HaveCount(1);
        secondRun.Should().BeEmpty();
        sentReminderKeys.Should().ContainSingle(k => k.StartsWith($"{calendarItem.Id:N}:{5 * 60}:"));
    }

    [Fact]
    public async Task CheckAndNotifyAsync_WhenCalendarAccessNotGranted_ReturnsEmpty()
    {
        var restrictedAccount = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "No Calendar Access",
            Address = "restricted@test.local",
            SenderName = "Restricted",
            IsCalendarAccessGranted = false
        };
        await _databaseService.Connection.InsertAsync(restrictedAccount, typeof(MailAccount));

        var restrictedCalendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = restrictedAccount.Id,
            Name = "Restricted Calendar",
            TimeZone = "UTC",
            IsPrimary = true,
            BackgroundColorHex = "#111111",
            TextColorHex = "#FFFFFF"
        };
        await _calendarService.InsertAccountCalendarAsync(restrictedCalendar);

        var nowLocal = new DateTime(2026, 1, 1, 10, 0, 0);
        var lastCheckLocal = nowLocal.AddSeconds(-30);

        await CreateCalendarItemWithReminderAsync(
            startDate: nowLocal.AddMinutes(5),
            reminderDurationInSeconds: 5 * 60,
            reminderType: CalendarItemReminderType.Popup,
            calendarId: restrictedCalendar.Id);

        HashSet<string> sentReminderKeys = [];

        var due = await _calendarService.CheckAndNotifyAsync(lastCheckLocal, nowLocal, sentReminderKeys);

        due.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAndNotifyAsync_WhenReminderTypeIsEmail_ReturnsEmpty()
    {
        var nowLocal = new DateTime(2026, 1, 1, 10, 0, 0);
        var lastCheckLocal = nowLocal.AddSeconds(-30);

        await CreateCalendarItemWithReminderAsync(
            startDate: nowLocal.AddMinutes(5),
            reminderDurationInSeconds: 5 * 60,
            reminderType: CalendarItemReminderType.Email);

        HashSet<string> sentReminderKeys = [];

        var due = await _calendarService.CheckAndNotifyAsync(lastCheckLocal, nowLocal, sentReminderKeys);

        due.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAndNotifyAsync_WhenItemIsRecurringParent_ReturnsEmpty()
    {
        var nowLocal = new DateTime(2026, 1, 1, 10, 0, 0);
        var lastCheckLocal = nowLocal.AddSeconds(-30);

        await CreateCalendarItemWithReminderAsync(
            startDate: nowLocal.AddMinutes(5),
            reminderDurationInSeconds: 5 * 60,
            reminderType: CalendarItemReminderType.Popup,
            recurrence: "RRULE:FREQ=DAILY;COUNT=5");

        HashSet<string> sentReminderKeys = [];

        var due = await _calendarService.CheckAndNotifyAsync(lastCheckLocal, nowLocal, sentReminderKeys);

        due.Should().BeEmpty();
    }


    [Fact]
    public async Task CheckAndNotifyAsync_WhenItemIsSnoozed_TriggersAtSnoozedTime()
    {
        var nowLocal = new DateTime(2026, 1, 1, 10, 0, 0);
        var lastCheckLocal = nowLocal.AddSeconds(-30);

        var calendarItem = await CreateCalendarItemWithReminderAsync(
            startDate: nowLocal.AddMinutes(5),
            reminderDurationInSeconds: 5 * 60,
            reminderType: CalendarItemReminderType.Popup);

        await _calendarService.SnoozeCalendarItemAsync(calendarItem.Id, nowLocal.AddMinutes(10));

        HashSet<string> sentReminderKeys = [];

        var dueAtOriginalTrigger = await _calendarService.CheckAndNotifyAsync(lastCheckLocal, nowLocal, sentReminderKeys);
        dueAtOriginalTrigger.Should().BeEmpty();

        var snoozeTriggerWindowStart = nowLocal.AddMinutes(10).AddSeconds(-30);
        var snoozeTriggerWindowEnd = nowLocal.AddMinutes(10);

        var dueAtSnoozeTime = await _calendarService.CheckAndNotifyAsync(snoozeTriggerWindowStart, snoozeTriggerWindowEnd, sentReminderKeys);

        dueAtSnoozeTime.Should().HaveCount(1);
        dueAtSnoozeTime[0].CalendarItem.Id.Should().Be(calendarItem.Id);
        dueAtSnoozeTime[0].ReminderKey.Should().StartWith($"{calendarItem.Id:N}:{5 * 60}:");
    }

    private async Task<CalendarItem> CreateCalendarItemWithReminderAsync(
        DateTime startDate,
        long reminderDurationInSeconds,
        CalendarItemReminderType reminderType,
        Guid? calendarId = null,
        string? recurrence = null)
    {
        var item = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Reminder Test Event",
            StartDate = startDate,
            StartTimeZone = string.Empty,
            EndTimeZone = string.Empty,
            DurationInSeconds = 60 * 30,
            CalendarId = calendarId ?? _testCalendar.Id,
            IsHidden = false,
            Recurrence = recurrence ?? string.Empty
        };

        await _calendarService.CreateNewCalendarItemAsync(item, null);

        await _calendarService.SaveRemindersAsync(item.Id,
        [
            new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = item.Id,
                DurationInSeconds = reminderDurationInSeconds,
                ReminderType = reminderType
            }
        ]);

        return item;
    }
}
