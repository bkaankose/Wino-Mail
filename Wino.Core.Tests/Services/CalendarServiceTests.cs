using FluentAssertions;
using Itenso.TimePeriod;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>
/// Tests for CalendarService, focusing on the GetCalendarEventsAsync method
/// which handles both regular and recurring events with RFC 5545 patterns.
/// </summary>
public class CalendarServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private CalendarService _calendarService = null!;
    private AccountCalendar _testCalendar = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _calendarService = new CalendarService(_databaseService);

        // Create a test calendar
        _testCalendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Name = "Test Calendar",
            TimeZone = "UTC",
            IsPrimary = true,
            BackgroundColorHex = "#FF5733",
            TextColorHex = "#FFFFFF"
        };

        await _calendarService.InsertAccountCalendarAsync(_testCalendar);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithNoEvents_ReturnsEmptyList()
    {
        // Arrange
        var period = new TimeRange(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(7));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithSingleNonRecurringEvent_ReturnsEvent()
    {
        // Arrange
        var startDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var calendarItem = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Team Meeting",
            Description = "Weekly sync",
            StartDate = startDate,
            DurationInSeconds = 3600, // 1 hour
            CalendarId = _testCalendar.Id,
            IsHidden = false
        };

        await _calendarService.CreateNewCalendarItemAsync(calendarItem, null);

        var period = new TimeRange(
            new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 16, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Team Meeting");
        result[0].StartDate.Should().Be(startDate);
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithNonRecurringEvent_OutsidePeriod_ReturnsEmpty()
    {
        // Arrange
        var startDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var calendarItem = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Team Meeting",
            StartDate = startDate,
            DurationInSeconds = 3600,
            CalendarId = _testCalendar.Id,
            IsHidden = false
        };

        await _calendarService.CreateNewCalendarItemAsync(calendarItem, null);

        // Query for a different week
        var period = new TimeRange(
            new DateTime(2025, 1, 22, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 29, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithDailyRecurringEvent_ReturnsMultipleOccurrences()
    {
        // Arrange - Create a daily recurring event starting Jan 15, 2025
        var startDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var recurringEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Daily Standup",
            Description = "Daily team sync",
            StartDate = startDate,
            DurationInSeconds = 1800, // 30 minutes
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            // Daily recurrence pattern (RFC 5545)
            Recurrence = "RRULE:FREQ=DAILY;COUNT=5"
        };

        await _calendarService.CreateNewCalendarItemAsync(recurringEvent, null);

        // Query for the week containing the recurring events
        var period = new TimeRange(
            new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().HaveCount(5, "because the event recurs daily for 5 days");
        result.Should().AllSatisfy(e =>
        {
            e.Title.Should().Be("Daily Standup");
            e.DurationInSeconds.Should().Be(1800);
            e.IsRecurringChild.Should().BeTrue();
            e.IsOccurrence.Should().BeTrue();
        });

        // Verify the dates are sequential
        var dates = result.Select(e => e.StartDate.Date).OrderBy(d => d).ToList();
        dates.Should().HaveCount(5);
        dates[0].Should().Be(new DateTime(2025, 1, 15).Date);
        dates[1].Should().Be(new DateTime(2025, 1, 16).Date);
        dates[2].Should().Be(new DateTime(2025, 1, 17).Date);
        dates[3].Should().Be(new DateTime(2025, 1, 18).Date);
        dates[4].Should().Be(new DateTime(2025, 1, 19).Date);
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithWeeklyRecurringEvent_ReturnsCorrectOccurrences()
    {
        // Arrange - Create a weekly recurring event on Mondays
        var startDate = new DateTime(2025, 1, 6, 14, 0, 0, DateTimeKind.Utc); // Monday
        var recurringEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Weekly Review",
            StartDate = startDate,
            DurationInSeconds = 3600, // 1 hour
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            // Weekly recurrence on Mondays
            Recurrence = "RRULE:FREQ=WEEKLY;BYDAY=MO;COUNT=4"
        };

        await _calendarService.CreateNewCalendarItemAsync(recurringEvent, null);

        // Query for a month
        var period = new TimeRange(
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().HaveCount(4, "because the event recurs weekly for 4 weeks");
        result.Should().AllSatisfy(e =>
        {
            e.Title.Should().Be("Weekly Review");
            e.StartDate.DayOfWeek.Should().Be(DayOfWeek.Monday);
        });
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithRecurringEventAndException_ExcludesException()
    {
        // Arrange - Create a daily recurring event
        var startDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var recurringEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Daily Meeting",
            StartDate = startDate,
            DurationInSeconds = 1800,
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            Recurrence = "RRULE:FREQ=DAILY;COUNT=5"
        };

        await _calendarService.CreateNewCalendarItemAsync(recurringEvent, null);

        // Create an exception instance for Jan 17 (cancelled)
        var exceptionInstance = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Daily Meeting (Cancelled)",
            StartDate = new DateTime(2025, 1, 17, 10, 0, 0, DateTimeKind.Utc),
            DurationInSeconds = 1800,
            CalendarId = _testCalendar.Id,
            RecurringCalendarItemId = recurringEvent.Id,
            IsHidden = true // Cancelled/hidden
        };

        await _calendarService.CreateNewCalendarItemAsync(exceptionInstance, null);

        var period = new TimeRange(
            new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().HaveCount(4, "because one occurrence is cancelled/hidden");
        result.Should().NotContain(e => e.StartDate.Date == new DateTime(2025, 1, 17).Date);
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithRecurringEventAndModifiedException_ReturnsModifiedVersion()
    {
        // Arrange - Create a daily recurring event
        var startDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var recurringEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Daily Meeting",
            StartDate = startDate,
            DurationInSeconds = 1800,
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            Recurrence = "RRULE:FREQ=DAILY;COUNT=5"
        };

        await _calendarService.CreateNewCalendarItemAsync(recurringEvent, null);

        // Create a modified exception instance for Jan 17 (time and duration changed)
        // The exception starts at 10:00 just like the original occurrence
        var modifiedException = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Daily Meeting (Rescheduled)",
            StartDate = new DateTime(2025, 1, 17, 10, 0, 0, DateTimeKind.Utc), // Same time, different properties
            DurationInSeconds = 3600, // Different duration (1 hour instead of 30 min)
            CalendarId = _testCalendar.Id,
            RecurringCalendarItemId = recurringEvent.Id,
            IsHidden = false
        };

        await _calendarService.CreateNewCalendarItemAsync(modifiedException, null);

        var period = new TimeRange(
            new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().HaveCount(5, "4 normal occurrences + 1 modified exception");
        
        // Check the modified exception - it should have the updated duration
        var jan17Events = result.Where(e => e.StartDate.Date == new DateTime(2025, 1, 17).Date).ToList();
        jan17Events.Should().HaveCount(1, "only the modified exception should appear for Jan 17");
        
        var modifiedEvent = jan17Events.First();
        modifiedEvent.Title.Should().Be("Daily Meeting (Rescheduled)");
        modifiedEvent.DurationInSeconds.Should().Be(3600);
        modifiedEvent.IsRecurringChild.Should().BeTrue();
        modifiedEvent.IsOccurrence.Should().BeFalse();
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithHiddenEvent_ExcludesFromResults()
    {
        // Arrange
        var startDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var hiddenEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Hidden Event",
            StartDate = startDate,
            DurationInSeconds = 3600,
            CalendarId = _testCalendar.Id,
            IsHidden = true
        };

        await _calendarService.CreateNewCalendarItemAsync(hiddenEvent, null);

        var period = new TimeRange(
            new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 16, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().BeEmpty("because hidden events should be excluded");
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithAllDayEvent_ReturnsEvent()
    {
        // Arrange
        var startDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc); // Midnight
        var allDayEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Company Holiday",
            StartDate = startDate,
            DurationInSeconds = 86400, // 24 hours
            CalendarId = _testCalendar.Id,
            IsHidden = false
        };

        await _calendarService.CreateNewCalendarItemAsync(allDayEvent, null);

        var period = new TimeRange(
            new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 16, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Company Holiday");
        result[0].IsAllDayEvent.Should().BeTrue();
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithMultipleCalendars_ReturnsOnlyRequestedCalendarEvents()
    {
        // Arrange - Create another calendar
        var secondCalendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = _testCalendar.AccountId,
            Name = "Second Calendar",
            TimeZone = "UTC",
            IsPrimary = false,
            BackgroundColorHex = "#00FF00",
            TextColorHex = "#000000"
        };

        await _calendarService.InsertAccountCalendarAsync(secondCalendar);

        // Add events to both calendars
        var startDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        
        var event1 = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Calendar 1 Event",
            StartDate = startDate,
            DurationInSeconds = 3600,
            CalendarId = _testCalendar.Id,
            IsHidden = false
        };

        var event2 = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Calendar 2 Event",
            StartDate = startDate,
            DurationInSeconds = 3600,
            CalendarId = secondCalendar.Id,
            IsHidden = false
        };

        await _calendarService.CreateNewCalendarItemAsync(event1, null);
        await _calendarService.CreateNewCalendarItemAsync(event2, null);

        var period = new TimeRange(
            new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 16, 0, 0, 0, DateTimeKind.Utc));

        // Act - Query only the first calendar
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Calendar 1 Event");
        result[0].CalendarId.Should().Be(_testCalendar.Id);
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithRecurringEventWithUNTIL_StopsAfterUntilDate()
    {
        // Arrange - Create two weekly recurring events with same pattern
        // Event 1: Has UNTIL date of Nov 13, 2025 (should stop after this date)
        // Event 2: No UNTIL date (continues indefinitely)
        
        var startDate = new DateTime(2025, 10, 10, 14, 0, 0, DateTimeKind.Utc); // Friday, Oct 10, 2025

        // Event with UNTIL - should stop on Nov 13, 2025
        var eventWithUntil = new CalendarItem
        {
            Id = Guid.NewGuid(),
            RemoteEventId = "event-with-until-123",
            Title = "Weekly Meeting (Until Nov 13)",
            StartDate = startDate,
            DurationInSeconds = 3600, // 1 hour
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            // Weekly on Fridays, until November 13, 2025
            Recurrence = "RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=FR;UNTIL=20251113"
        };

        // Event without UNTIL - continues indefinitely
        var eventWithoutUntil = new CalendarItem
        {
            Id = Guid.NewGuid(),
            RemoteEventId = "event-without-until-456",
            Title = "Weekly Meeting (No End)",
            StartDate = startDate,
            DurationInSeconds = 3600, // 1 hour
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            // Weekly on Fridays, no end date
            Recurrence = "RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=FR"
        };

        await _calendarService.CreateNewCalendarItemAsync(eventWithUntil, null);
        await _calendarService.CreateNewCalendarItemAsync(eventWithoutUntil, null);

        // Query for a period AFTER the UNTIL date (Nov 20 - Nov 30, 2025)
        // This is past November 13, so only the event without UNTIL should appear
        var periodAfterUntil = new TimeRange(
            new DateTime(2025, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 11, 30, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var resultAfterUntil = await _calendarService.GetCalendarEventsAsync(_testCalendar, periodAfterUntil);

        // Assert - Only the event without UNTIL should appear
        // In Nov 20-30 period, there are 2 Fridays: Nov 21 and Nov 28
        // Both should only be from the event WITHOUT UNTIL
        resultAfterUntil.Should().HaveCount(2, "there are 2 Fridays in Nov 20-30 period");
        resultAfterUntil.Should().AllSatisfy(e =>
        {
            e.Title.Should().Be("Weekly Meeting (No End)");
            e.RecurringCalendarItemId.Should().Be(eventWithoutUntil.Id);
        });
        
        // Verify NO occurrences from the event with UNTIL appear after the UNTIL date
        var withUntilOccurrences = resultAfterUntil.Where(e => e.RecurringCalendarItemId == eventWithUntil.Id).ToList();
        withUntilOccurrences.Should().BeEmpty("the event with UNTIL=Nov 13 should not appear after that date");

        // Query for a period BEFORE the UNTIL date (Oct 10 - Nov 10, 2025)
        // Both events should appear since we're before the UNTIL date
        var periodBeforeUntil = new TimeRange(
            new DateTime(2025, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 11, 10, 0, 0, 0, DateTimeKind.Utc));

        var resultBeforeUntil = await _calendarService.GetCalendarEventsAsync(_testCalendar, periodBeforeUntil);

        // Should have occurrences from both events
        // From Oct 10 to Nov 10, Fridays are: Oct 10, 17, 24, 31, Nov 7
        // That's 5 Fridays, so we expect 10 total (5 from each event)
        resultBeforeUntil.Should().HaveCount(10, "both events should have 5 occurrences each in this period");
        
        var untilEventOccurrences = resultBeforeUntil.Where(e => e.RecurringCalendarItemId == eventWithUntil.Id).ToList();
        var noUntilEventOccurrences = resultBeforeUntil.Where(e => e.RecurringCalendarItemId == eventWithoutUntil.Id).ToList();
        
        untilEventOccurrences.Should().HaveCount(5);
        noUntilEventOccurrences.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetCalendarEventsAsync_WithDuplicateRecurringEvents_OnlyShowsNonExpiredOccurrences()
    {
        // Arrange - Simulates the scenario where you have the same recurring event
        // synced twice with different RemoteEventIds, one with UNTIL and one without
        
        var startDate = new DateTime(2025, 10, 10, 14, 0, 0, DateTimeKind.Utc); // Friday, Oct 10, 2025

        // First sync: Event with UNTIL (older version that expired)
        var expiredEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            RemoteEventId = "recurring-event-v1",
            Title = "Team Standup",
            StartDate = startDate,
            DurationInSeconds = 1800, // 30 min
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            Recurrence = "RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=FR;UNTIL=20251113T000000Z"
        };

        // Second sync: Same event but without UNTIL (updated version)
        var activeEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            RemoteEventId = "recurring-event-v2",
            Title = "Team Standup",
            StartDate = startDate,
            DurationInSeconds = 1800,
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            Recurrence = "RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=FR" // No UNTIL - continues indefinitely
        };

        await _calendarService.CreateNewCalendarItemAsync(expiredEvent, null);
        await _calendarService.CreateNewCalendarItemAsync(activeEvent, null);

        // Query for December 2025 (well after the UNTIL date of Nov 13)
        var decemberPeriod = new TimeRange(
            new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc));

        // Act
        var decemberResults = await _calendarService.GetCalendarEventsAsync(_testCalendar, decemberPeriod);

        // Assert - Should only see occurrences from the active event (without UNTIL)
        // December 2025 has Fridays on: 5, 12, 19, 26
        decemberResults.Should().HaveCount(4, "December has 4 Fridays");
        decemberResults.Should().AllSatisfy(e =>
        {
            e.RecurringCalendarItemId.Should().Be(activeEvent.Id, "only the event without UNTIL should appear");
            e.Title.Should().Be("Team Standup");
        });

        // Verify the expired event doesn't contribute any occurrences
        var expiredOccurrences = decemberResults.Where(e => e.RecurringCalendarItemId == expiredEvent.Id).ToList();
        expiredOccurrences.Should().BeEmpty("the expired event with UNTIL=Nov 13 should not generate occurrences in December");

        // Also test a period that spans the UNTIL boundary (November 1-30)
        var novemberPeriod = new TimeRange(
            new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 11, 30, 23, 59, 59, DateTimeKind.Utc));

        var novemberResults = await _calendarService.GetCalendarEventsAsync(_testCalendar, novemberPeriod);

        // November 2025 Fridays: 7, 14, 21, 28
        // Event with UNTIL stops on Nov 13, so Nov 7 is the last occurrence for that one
        // Event without UNTIL continues, so it has all 4 occurrences
        
        var expiredEventInNov = novemberResults.Where(e => e.RecurringCalendarItemId == expiredEvent.Id).ToList();
        var activeEventInNov = novemberResults.Where(e => e.RecurringCalendarItemId == activeEvent.Id).ToList();

        expiredEventInNov.Should().HaveCount(1, "expired event only appears on Nov 7 (before UNTIL=20251113)");
        expiredEventInNov[0].StartDate.Day.Should().Be(7);

        activeEventInNov.Should().HaveCount(4, "active event appears on all 4 Fridays");
    }
}
