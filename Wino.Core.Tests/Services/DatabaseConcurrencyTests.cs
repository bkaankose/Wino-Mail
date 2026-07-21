using FluentAssertions;
using SQLite;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class DatabaseConcurrencyTests
{
    [Fact]
    public async Task ReadOnlyConnectionReadsWalWriterAndRejectsMutations()
    {
        var testFolder = Path.Combine(Path.GetTempPath(), $"wino-dual-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testFolder);
        var configuration = new ApplicationConfiguration
        {
            ApplicationDataFolderPath = testFolder,
            PublisherSharedFolderPath = testFolder,
            ApplicationTempFolderPath = testFolder,
        };
        var writer = new DatabaseService(configuration, DatabaseAccessMode.ReadWrite);
        var reader = new DatabaseService(configuration, DatabaseAccessMode.ReadOnly);

        try
        {
            await writer.InitializeAsync();
            await writer.Connection.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS DualConnectionProbe (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL)");
            await writer.Connection.ExecuteAsync(
                "INSERT INTO DualConnectionProbe (Value) VALUES (?)",
                "visible");

            await reader.InitializeAsync();

            reader.IsReadOnly.Should().BeTrue();
            var value = await reader.Connection.ExecuteScalarAsync<string>(
                "SELECT Value FROM DualConnectionProbe LIMIT 1");
            value.Should().Be("visible");

            await writer.Connection.ExecuteAsync(
                "INSERT INTO DualConnectionProbe (Value) VALUES (?)",
                "written-while-reader-open");
            var count = await reader.Connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM DualConnectionProbe");
            count.Should().Be(2);

            await Assert.ThrowsAsync<SQLiteException>(() =>
                reader.Connection.ExecuteAsync(
                    "INSERT INTO DualConnectionProbe (Value) VALUES (?)",
                    "rejected"));
        }
        finally
        {
            if (reader.IsAvailable)
            {
                await reader.Connection.CloseAsync();
            }

            if (writer.IsAvailable)
            {
                await writer.Connection.CloseAsync();
            }

            Directory.Delete(testFolder, recursive: true);
        }
    }
}
