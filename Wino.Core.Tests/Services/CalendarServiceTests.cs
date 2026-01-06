using FluentAssertions;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>
/// Tests for CalendarService, focusing on the GetCalendarEventsAsync method.
/// Note: Recurring event occurrences are now synced from the server as individual instances,
/// not calculated locally from recurrence patterns.
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
    public async Task GetCalendarEventsAsync_WithRecurringEventInstances_ReturnsAllInstancesInPeriod()
    {
        // Arrange - Simulate synced recurring event instances (as they would come from server)
        var parentId = Guid.NewGuid();
        var startDate1 = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var startDate2 = new DateTime(2025, 1, 16, 10, 0, 0, DateTimeKind.Utc);
        var startDate3 = new DateTime(2025, 1, 17, 10, 0, 0, DateTimeKind.Utc);

        // Parent series master (typically hidden or outside display range)
        var parentEvent = new CalendarItem
        {
            Id = parentId,
            Title = "Daily Standup",
            Description = "Daily team sync",
            StartDate = startDate1,
            DurationInSeconds = 1800, // 30 minutes
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            Recurrence = "RRULE:FREQ=DAILY;COUNT=3"
        };

        // Individual occurrence instances (as synced from server)
        var instance1 = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Daily Standup",
            StartDate = startDate1,
            DurationInSeconds = 1800,
            CalendarId = _testCalendar.Id,
            RecurringCalendarItemId = parentId,
            IsHidden = false
        };

        var instance2 = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Daily Standup",
            StartDate = startDate2,
            DurationInSeconds = 1800,
            CalendarId = _testCalendar.Id,
            RecurringCalendarItemId = parentId,
            IsHidden = false
        };

        var instance3 = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Daily Standup",
            StartDate = startDate3,
            DurationInSeconds = 1800,
            CalendarId = _testCalendar.Id,
            RecurringCalendarItemId = parentId,
            IsHidden = false
        };

        await _calendarService.CreateNewCalendarItemAsync(parentEvent, null);
        await _calendarService.CreateNewCalendarItemAsync(instance1, null);
        await _calendarService.CreateNewCalendarItemAsync(instance2, null);
        await _calendarService.CreateNewCalendarItemAsync(instance3, null);

        var period = new TimeRange(
            new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 18, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert - Should return parent + 3 instances = 4 total
        result.Should().HaveCount(4, "parent event plus 3 instances");
        result.Where(e => e.Title == "Daily Standup").Should().HaveCount(4);
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
    public async Task GetCalendarEventsAsync_WithRecurringChildEvent_ReturnsChildAsRecurringChild()
    {
        // Arrange - Create a parent and child event
        var parentId = Guid.NewGuid();
        var parentEvent = new CalendarItem
        {
            Id = parentId,
            Title = "Parent Recurring Event",
            StartDate = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            DurationInSeconds = 3600,
            CalendarId = _testCalendar.Id,
            IsHidden = false,
            Recurrence = "RRULE:FREQ=DAILY"
        };

        var childEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Occurrence Instance",
            StartDate = new DateTime(2025, 1, 16, 10, 0, 0, DateTimeKind.Utc),
            DurationInSeconds = 3600,
            CalendarId = _testCalendar.Id,
            RecurringCalendarItemId = parentId,
            IsHidden = false
        };

        await _calendarService.CreateNewCalendarItemAsync(parentEvent, null);
        await _calendarService.CreateNewCalendarItemAsync(childEvent, null);

        var period = new TimeRange(
            new DateTime(2025, 1, 16, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 17, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var result = await _calendarService.GetCalendarEventsAsync(_testCalendar, period);

        // Assert
        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Occurrence Instance");
        result[0].IsRecurringChild.Should().BeTrue();
        result[0].RecurringCalendarItemId.Should().Be(parentId);
    }
}
