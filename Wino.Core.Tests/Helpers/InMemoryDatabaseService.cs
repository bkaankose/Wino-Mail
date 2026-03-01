using SQLite;
using System.IO;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Services;

namespace Wino.Core.Tests.Helpers;

/// <summary>
/// In-memory database service for testing purposes.
/// Creates a temporary SQLite database in memory that is destroyed after tests complete.
/// </summary>
public class InMemoryDatabaseService : IDatabaseService
{
    private readonly string _databasePath;
    public SQLiteAsyncConnection Connection { get; private set; }

    public InMemoryDatabaseService()
    {
        // Use a unique temporary file per test instance for stable async access.
        _databasePath = Path.Combine(Path.GetTempPath(), $"wino-tests-{Guid.NewGuid():N}.db");
        Connection = new SQLiteAsyncConnection(_databasePath);
    }

    public async Task InitializeAsync()
    {
        await CreateTablesAsync();
    }

    private async Task CreateTablesAsync()
    {
        // Keep table creation sequential for in-memory SQLite to avoid connection contention.
        await Connection.CreateTableAsync<MailCopy>();
        await Connection.CreateTableAsync<MailItemFolder>();
        await Connection.CreateTableAsync<MailAccount>();
        await Connection.CreateTableAsync<AccountContact>();
        await Connection.CreateTableAsync<CustomServerInformation>();
        await Connection.CreateTableAsync<AccountSignature>();
        await Connection.CreateTableAsync<MergedInbox>();
        await Connection.CreateTableAsync<MailAccountPreferences>();
        await Connection.CreateTableAsync<MailAccountAlias>();
        await Connection.CreateTableAsync<Thumbnail>();
        await Connection.CreateTableAsync<ContactGroup>();
        await Connection.CreateTableAsync<ContactGroupMember>();
        await Connection.CreateTableAsync<KeyboardShortcut>();
        await Connection.CreateTableAsync<AccountCalendar>();
        await Connection.CreateTableAsync<CalendarEventAttendee>();
        await Connection.CreateTableAsync<CalendarItem>();
        await Connection.CreateTableAsync<CalendarAttachment>();
        await Connection.CreateTableAsync<Reminder>();
        await Connection.CreateTableAsync<MailInvitationCalendarMapping>();
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection != null)
        {
            await Connection.CloseAsync();
            Connection = null!;
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
