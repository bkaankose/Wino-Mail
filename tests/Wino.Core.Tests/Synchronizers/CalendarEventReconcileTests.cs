using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// After a calendar is re-anchored to a new window, a full download replaces the
/// previous delta. Rows inside the window that the provider no longer returns must go,
/// everything else must stay.
/// </summary>
public sealed class CalendarEventReconcileTests
{
    [Fact]
    public async Task ReconcileCalendarEventsAsync_DeletesOnlyMissingRowsInsideWindow()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var calendarService = new CalendarService(database);
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Name = "Calendar",
            TimeZone = "UTC"
        };
        await database.Connection.InsertAsync(new MailAccount { Id = calendar.AccountId, Address = "user@example.test" });
        await calendarService.InsertAccountCalendarAsync(calendar);

        var trackingId = Guid.NewGuid();
        var kept = CreateItem(calendar, "remote-1", new DateTime(2026, 9, 15, 10, 0, 0));
        var missing = CreateItem(calendar, "remote-2", new DateTime(2026, 9, 16, 10, 0, 0));
        var tracked = CreateItem(calendar, $"remote-3::{trackingId:N}", new DateTime(2026, 9, 17, 10, 0, 0));
        var placeholder = CreateItem(calendar, Guid.NewGuid().ToString("N"), new DateTime(2026, 9, 18, 10, 0, 0));
        var localPlaceholder = CreateItem(calendar, $"local-{Guid.NewGuid():N}", new DateTime(2026, 9, 18, 12, 0, 0));
        var outsideWindow = CreateItem(calendar, "remote-9", new DateTime(2026, 11, 15, 10, 0, 0));

        foreach (var item in new[] { kept, missing, tracked, placeholder, localPlaceholder, outsideWindow })
            await calendarService.CreateNewCalendarItemAsync(item, null);

        var processor = new ImapChangeProcessor(
            database,
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            calendarService,
            Mock.Of<IMimeFileService>(),
            Mock.Of<ICalendarIcsFileService>());

        await processor.ReconcileCalendarEventsAsync(
            calendar,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new HashSet<string>(StringComparer.Ordinal) { "remote-1", "remote-3" });

        var remainingIds = (await database.Connection.Table<CalendarItem>().ToListAsync()).Select(item => item.Id).ToList();

        remainingIds.Should().BeEquivalentTo([kept.Id, tracked.Id, placeholder.Id, localPlaceholder.Id, outsideWindow.Id]);
        remainingIds.Should().NotContain(missing.Id);
    }

    private static CalendarItem CreateItem(AccountCalendar calendar, string remoteEventId, DateTime startUtc) => new()
    {
        Id = Guid.NewGuid(),
        CalendarId = calendar.Id,
        RemoteEventId = remoteEventId,
        StartDate = startUtc,
        DurationInSeconds = 3600,
        StartTimeZone = "UTC",
        EndTimeZone = "UTC",
        Title = remoteEventId
    };
}
