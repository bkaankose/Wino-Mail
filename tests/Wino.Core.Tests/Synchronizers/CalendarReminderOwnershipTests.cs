using FluentAssertions;
using Microsoft.Graph.Models;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;
using Reminder = Wino.Core.Domain.Entities.Calendar.Reminder;

namespace Wino.Core.Tests.Synchronizers;

public sealed class CalendarReminderOwnershipTests : IAsyncLifetime
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
            Name = "Outlook",
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

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task OutlookRefresh_WhenEventAlreadyExists_PreservesAllLocalReminders()
    {
        var item = new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = _calendar.Id,
            AssignedCalendar = _calendar,
            RemoteEventId = "remote-event",
            Title = "Planning",
            StartDate = new DateTime(2026, 7, 28, 10, 0, 0),
            DurationInSeconds = 60 * 60,
            StartTimeZone = "UTC",
            EndTimeZone = "UTC",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _calendarService.CreateNewCalendarItemAsync(item, null);
        await _calendarService.SaveRemindersAsync(item.Id,
        [
            new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = item.Id,
                DurationInSeconds = 15 * 60,
                ReminderType = CalendarItemReminderType.Popup
            },
            new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = item.Id,
                DurationInSeconds = 30 * 60,
                ReminderType = CalendarItemReminderType.Popup
            }
        ]);

        var processor = new OutlookChangeProcessor(
            _databaseService,
            Mock.Of<IFolderService>(),
            _calendarService,
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IMimeFileService>());

        var remoteEvent = new Event
        {
            Id = "remote-event",
            Subject = "Planning",
            Start = new DateTimeTimeZone
            {
                DateTime = "2026-07-28T10:00:00",
                TimeZone = "UTC"
            },
            End = new DateTimeTimeZone
            {
                DateTime = "2026-07-28T11:00:00",
                TimeZone = "UTC"
            },
            IsReminderOn = true,
            ReminderMinutesBeforeStart = 15,
            Type = EventType.SingleInstance
        };

        await processor.ManageCalendarEventAsync(remoteEvent, _calendar, _account);

        var reminders = await _calendarService.GetRemindersAsync(item.Id);
        reminders.Select(r => r.DurationInSeconds)
            .Should()
            .BeEquivalentTo([15 * 60L, 30 * 60L]);
    }

    [Fact]
    public async Task PersistCreatedCalendarEventAsync_SavesEveryLocalReminderAfterRemoteCreate()
    {
        var item = new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = _calendar.Id,
            AssignedCalendar = _calendar,
            Title = "Planning",
            StartDate = new DateTime(2026, 7, 28, 10, 0, 0),
            DurationInSeconds = 60 * 60,
            StartTimeZone = "UTC",
            EndTimeZone = "UTC",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var reminders = new List<Reminder>
        {
            new()
            {
                Id = Guid.NewGuid(),
                CalendarItemId = item.Id,
                DurationInSeconds = 15 * 60,
                ReminderType = CalendarItemReminderType.Popup
            },
            new()
            {
                Id = Guid.NewGuid(),
                CalendarItemId = item.Id,
                DurationInSeconds = 30 * 60,
                ReminderType = CalendarItemReminderType.Popup
            }
        };
        var processor = CreateProcessor();

        await processor.PersistCreatedCalendarEventAsync(
            item,
            [],
            reminders,
            $"remote-event::{item.Id:N}");

        var persistedItem = await _calendarService.GetCalendarItemAsync(item.Id);
        var persistedReminders = await _calendarService.GetRemindersAsync(item.Id);

        persistedItem.RemoteEventId.Should().Be($"remote-event::{item.Id:N}");
        persistedReminders.Select(r => r.DurationInSeconds)
            .Should()
            .BeEquivalentTo([15 * 60L, 30 * 60L]);
    }

    [Fact]
    public async Task OutlookRefresh_AfterThreeMutableIdCreates_KeepsThreeLocalEvents()
    {
        var processor = CreateProcessor();
        var localIds = new List<Guid>();

        for (var index = 0; index < 3; index++)
        {
            var item = new CalendarItem
            {
                Id = Guid.NewGuid(),
                CalendarId = _calendar.Id,
                AssignedCalendar = _calendar,
                Title = $"Event {index}",
                StartDate = new DateTime(2026, 7, 28, 10, 0, 0),
                DurationInSeconds = 3600,
                StartTimeZone = "UTC",
                EndTimeZone = "UTC"
            };
            localIds.Add(item.Id);
            await processor.PersistCreatedCalendarEventAsync(item, [], [], $"mutable-{index}::{item.Id:N}");

            var remoteEvent = new Event
            {
                Id = $"immutable-{index}",
                TransactionId = item.Id.ToString("N"),
                Subject = item.Title,
                Type = EventType.SingleInstance,
                Start = new DateTimeTimeZone { DateTime = "2026-07-28T10:00:00", TimeZone = "UTC" },
                End = new DateTimeTimeZone { DateTime = "2026-07-28T11:00:00", TimeZone = "UTC" }
            };

            await processor.ManageCalendarEventAsync(remoteEvent, _calendar, _account);
            await processor.ManageCalendarEventAsync(remoteEvent, _calendar, _account);
        }

        var persisted = await _databaseService.Connection.Table<CalendarItem>().ToListAsync();
        persisted.Select(item => item.Id).Should().BeEquivalentTo(localIds);
        persisted.Should().OnlyContain(item => item.RemoteEventId.StartsWith("immutable-"));
    }

    [Fact]
    public async Task RemoteEventLookup_DistinguishesCaseAndLiteralUnderscores()
    {
        var processor = CreateProcessor();
        var item = new CalendarItem
        {
            Id = Guid.NewGuid(), CalendarId = _calendar.Id, AssignedCalendar = _calendar,
            Title = "Existing", StartDate = new DateTime(2026, 7, 28), DurationInSeconds = 3600
        };
        await processor.PersistCreatedCalendarEventAsync(item, [], [], $"ABCx123::{item.Id:N}");

        (await _calendarService.GetCalendarItemAsync(_calendar.Id, "ABC_123")).Should().BeNull();
        (await _calendarService.GetCalendarItemAsync(_calendar.Id, "abcx123")).Should().BeNull();
        (await _calendarService.GetCalendarItemAsync(_calendar.Id, "ABCx123"))!.Id.Should().Be(item.Id);
    }

    [Fact]
    public async Task OutlookMoveRefresh_ReconcilesExistingDuplicateAndPreservesLocalReminders()
    {
        var originalId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        foreach (var (id, remoteId) in new[] { (originalId, "mutable"), (duplicateId, "immutable") })
        {
            await _calendarService.CreateNewCalendarItemAsync(new CalendarItem
            {
                Id = id, CalendarId = _calendar.Id, AssignedCalendar = _calendar,
                RemoteEventId = $"{remoteId}::{originalId:N}", Title = "Moved event",
                StartDate = new DateTime(2026, 7, 28, 10, 0, 0), DurationInSeconds = 3600,
                StartTimeZone = "UTC", EndTimeZone = "UTC"
            }, null);
            await _calendarService.SaveRemindersAsync(id, [new Reminder
            {
                Id = Guid.NewGuid(), CalendarItemId = id,
                DurationInSeconds = id == originalId ? 900 : 1800,
                ReminderType = CalendarItemReminderType.Popup
            }]);
        }

        var moved = new Event
        {
            Id = "immutable", TransactionId = originalId.ToString("N"),
            Subject = "Moved event", Type = EventType.SingleInstance,
            Start = new DateTimeTimeZone { DateTime = "2026-07-28T14:00:00", TimeZone = "UTC" },
            End = new DateTimeTimeZone { DateTime = "2026-07-28T15:00:00", TimeZone = "UTC" }
        };
        var processor = CreateProcessor();
        await processor.ManageCalendarEventAsync(moved, _calendar, _account);
        await processor.ManageCalendarEventAsync(moved, _calendar, _account);

        var items = await _databaseService.Connection.Table<CalendarItem>().ToListAsync();
        items.Should().ContainSingle().Which.Id.Should().Be(originalId);
        items[0].StartDate.Should().Be(new DateTime(2026, 7, 28, 14, 0, 0));
        (await _calendarService.GetRemindersAsync(originalId)).Select(reminder => reminder.DurationInSeconds)
            .Should().BeEquivalentTo(new long[] { 900, 1800 });
        (await _calendarService.GetRemindersAsync(duplicateId)).Should().BeEmpty();
    }

    private OutlookChangeProcessor CreateProcessor() =>
        new(
            _databaseService,
            Mock.Of<IFolderService>(),
            _calendarService,
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IMimeFileService>());
}
