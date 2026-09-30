using FluentAssertions;
using TimeRange = Itenso.TimePeriod.TimeRange;
using Microsoft.Graph.Models;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;
using GoogleEvent = Google.Apis.Calendar.v3.Data.Event;
using GoogleEventDateTime = Google.Apis.Calendar.v3.Data.EventDateTime;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// Recurring and single event persistence for Outlook and Gmail change processors.
/// </summary>
public sealed class RecurringEventPersistenceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private CalendarService _calendarService = null!;
    private MailAccount _account = null!;
    private AccountCalendar _calendar = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _calendarService = new CalendarService(_databaseService);

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Account",
            Address = "user@example.com",
            SenderName = "User",
            IsCalendarAccessGranted = true
        };
        await _databaseService.Connection.InsertAsync(_account, typeof(MailAccount));

        _calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = _account.Id,
            RemoteCalendarId = "calendar",
            Name = "Calendar",
            TimeZone = "UTC",
            IsPrimary = true
        };
        await _calendarService.InsertAccountCalendarAsync(_calendar);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task Outlook_OccurrencesLinkToSeriesMasterCreatedInWino()
    {
        var masterId = Guid.NewGuid();
        await _calendarService.CreateNewCalendarItemAsync(new CalendarItem
        {
            Id = masterId,
            CalendarId = _calendar.Id,
            RemoteEventId = "series-master".WithClientTrackingId(masterId),
            Title = "Weekly",
            StartDate = new DateTime(2026, 3, 2, 9, 0, 0),
            DurationInSeconds = 1800,
            Recurrence = "RRULE:FREQ=WEEKLY"
        }, null);

        var processor = CreateOutlookProcessor();
        await processor.ManageCalendarEventAsync(CreateOutlookEvent("occurrence-1", EventType.Occurrence, 2, "series-master"), _calendar, _account);
        await processor.ManageCalendarEventAsync(CreateOutlookEvent("occurrence-2", EventType.Occurrence, 9, "series-master"), _calendar, _account);

        var visible = await GetVisibleEventsAsync();

        visible.Should().HaveCount(2);
        visible.Should().OnlyContain(e => e.RecurringCalendarItemId == masterId);
    }

    [Fact]
    public async Task Outlook_SeriesConvertedToSingleEvent_ClearsRecurrenceAndStaysVisible()
    {
        await _calendarService.CreateNewCalendarItemAsync(new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = _calendar.Id,
            RemoteEventId = "event",
            StartDate = new DateTime(2026, 3, 2, 9, 0, 0),
            DurationInSeconds = 1800,
            Recurrence = "RRULE:FREQ=DAILY"
        }, null);

        await CreateOutlookProcessor().ManageCalendarEventAsync(CreateOutlookEvent("event", EventType.SingleInstance, 2), _calendar, _account);

        var visible = await GetVisibleEventsAsync();
        visible.Should().ContainSingle().Which.Recurrence.Should().BeEmpty();
    }

    [Fact]
    public async Task Outlook_CancelledOccurrence_IsHidden()
    {
        var cancelled = CreateOutlookEvent("occurrence", EventType.Occurrence, 2, "series-master");
        cancelled.IsCancelled = true;

        await CreateOutlookProcessor().ManageCalendarEventAsync(cancelled, _calendar, _account);

        (await GetVisibleEventsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Gmail_InstancesLinkToParentAndParentStaysHidden()
    {
        var processor = CreateGmailProcessor();

        await processor.ManageCalendarEventAsync(CreateGoogleEvent("series", 2, recurrence: "RRULE:FREQ=DAILY;COUNT=2"), _calendar, _account);
        await processor.ManageCalendarEventAsync(CreateGoogleEvent("series_20260302T090000Z", 2, recurringEventId: "series"), _calendar, _account);
        await processor.ManageCalendarEventAsync(CreateGoogleEvent("series_20260303T090000Z", 3, recurringEventId: "series"), _calendar, _account);

        var parent = await _calendarService.GetCalendarItemAsync(_calendar.Id, "series");
        var visible = await GetVisibleEventsAsync();

        visible.Select(e => e.StartDate.Day).Should().BeEquivalentTo([2, 3]);
        visible.Should().OnlyContain(e => e.RecurringCalendarItemId == parent!.Id && e.Recurrence == string.Empty);
    }

    [Fact]
    public async Task Gmail_SeriesConvertedToSingleEvent_ClearsRecurrenceAndStaysVisible()
    {
        var processor = CreateGmailProcessor();

        await processor.ManageCalendarEventAsync(CreateGoogleEvent("event", 2, recurrence: "RRULE:FREQ=DAILY"), _calendar, _account);
        (await GetVisibleEventsAsync()).Should().BeEmpty();

        await processor.ManageCalendarEventAsync(CreateGoogleEvent("event", 2), _calendar, _account);

        (await GetVisibleEventsAsync()).Should().ContainSingle().Which.Recurrence.Should().BeEmpty();
    }

    private OutlookChangeProcessor CreateOutlookProcessor()
        => new(
            _databaseService,
            Mock.Of<IFolderService>(),
            _calendarService,
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IMimeFileService>());

    private GmailChangeProcessor CreateGmailProcessor()
        => new(
            _databaseService,
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            _calendarService,
            Mock.Of<IAccountService>(),
            Mock.Of<IMimeFileService>());

    private static Event CreateOutlookEvent(string id, EventType type, int day, string? seriesMasterId = null)
        => new()
        {
            Id = id,
            Subject = "Weekly",
            Type = type,
            SeriesMasterId = seriesMasterId,
            Start = new DateTimeTimeZone { DateTime = $"2026-03-{day:00}T09:00:00", TimeZone = "UTC" },
            End = new DateTimeTimeZone { DateTime = $"2026-03-{day:00}T09:30:00", TimeZone = "UTC" }
        };

    private static GoogleEvent CreateGoogleEvent(string id, int day, string? recurrence = null, string? recurringEventId = null)
        => new()
        {
            Id = id,
            Summary = "Daily",
            Status = "confirmed",
            RecurringEventId = recurringEventId,
            Recurrence = recurrence == null ? null : [recurrence],
            Start = new GoogleEventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(2026, 3, day, 9, 0, 0, TimeSpan.Zero), TimeZone = "UTC" },
            End = new GoogleEventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(2026, 3, day, 9, 30, 0, TimeSpan.Zero), TimeZone = "UTC" }
        };

    private Task<List<CalendarItem>> GetVisibleEventsAsync()
        => _calendarService.GetCalendarEventsAsync(_calendar, new TimeRange(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));
}
