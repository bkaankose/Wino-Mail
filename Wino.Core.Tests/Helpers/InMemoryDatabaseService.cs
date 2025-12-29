using SQLite;
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
    public SQLiteAsyncConnection Connection { get; private set; }

    public InMemoryDatabaseService()
    {
        // Use :memory: for a truly in-memory database or a temporary file
        Connection = new SQLiteAsyncConnection(":memory:");
    }

    public async Task InitializeAsync()
    {
        await CreateTablesAsync();
    }

    private async Task CreateTablesAsync()
    {
        await Task.WhenAll(
            Connection.CreateTableAsync<MailCopy>(),
            Connection.CreateTableAsync<MailItemFolder>(),
            Connection.CreateTableAsync<MailAccount>(),
            Connection.CreateTableAsync<AccountContact>(),
            Connection.CreateTableAsync<CustomServerInformation>(),
            Connection.CreateTableAsync<AccountSignature>(),
            Connection.CreateTableAsync<MergedInbox>(),
            Connection.CreateTableAsync<MailAccountPreferences>(),
            Connection.CreateTableAsync<MailAccountAlias>(),
            Connection.CreateTableAsync<Thumbnail>(),
            Connection.CreateTableAsync<KeyboardShortcut>(),
            Connection.CreateTableAsync<AccountCalendar>(),
            Connection.CreateTableAsync<CalendarEventAttendee>(),
            Connection.CreateTableAsync<CalendarItem>(),
            Connection.CreateTableAsync<Reminder>()
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection != null)
        {
            await Connection.CloseAsync();
            Connection = null!;
        }
    }
}
