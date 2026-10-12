#nullable enable

using FluentAssertions;
using SQLite;
using System.IO;
using Wino.Core.Domain.Entities.Mail;
using Xunit;

namespace Wino.Core.Tests.Models;

public class MailCopyCreationDateTests
{
    private static readonly DateTime ReceivedUtc = new(2026, 10, 11, 23, 16, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreationDate_Should_Read_Back_From_Database_As_Utc()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wino-tests-{Guid.NewGuid():N}.db");
        var connection = new SQLiteAsyncConnection(path);
        try
        {
            await connection.CreateTableAsync<MailCopy>();
            var mail = new MailCopy { UniqueId = Guid.NewGuid(), Id = "mail", CreationDate = ReceivedUtc };
            await connection.InsertAsync(mail);

            var stored = await connection.GetAsync<MailCopy>(mail.UniqueId);

            stored.CreationDate.Kind.Should().Be(DateTimeKind.Utc);
            stored.CreationDate.Should().Be(ReceivedUtc);
            new DateTimeOffset(stored.CreationDate).LocalDateTime.Date
                .Should().Be(ReceivedUtc.ToLocalTime().Date);
        }
        finally
        {
            await connection.CloseAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public void CreationDate_Should_Treat_Unspecified_Values_As_Utc()
    {
        var mail = new MailCopy { CreationDate = DateTime.SpecifyKind(ReceivedUtc, DateTimeKind.Unspecified) };

        mail.CreationDate.Kind.Should().Be(DateTimeKind.Utc);
        mail.CreationDate.Ticks.Should().Be(ReceivedUtc.Ticks);
    }

    [Fact]
    public void CreationDate_Should_Convert_Local_Values_To_Utc()
    {
        var mail = new MailCopy { CreationDate = ReceivedUtc.ToLocalTime() };

        mail.CreationDate.Kind.Should().Be(DateTimeKind.Utc);
        mail.CreationDate.Should().Be(ReceivedUtc);
    }
}
