using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Services;

public class RecipientHistoryService : BaseDatabaseService, IRecipientHistoryService
{
    // Counts add up, the latest interaction wins, and a real name replaces an address used as a name.
    // IsSuppressed is deliberately absent from the update so new mail never brings a hidden address back.
    private const string UpsertSql =
        "INSERT INTO RecipientHistory (Id, AccountId, NormalizedAddress, Address, DisplayName, SentCount, ReceivedCount, LastSentUtc, LastReceivedUtc, IsSuppressed) " +
        "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 0) " +
        "ON CONFLICT(AccountId, NormalizedAddress) DO UPDATE SET " +
        "SentCount = SentCount + excluded.SentCount, " +
        "ReceivedCount = ReceivedCount + excluded.ReceivedCount, " +
        "LastSentUtc = NULLIF(MAX(COALESCE(LastSentUtc, 0), COALESCE(excluded.LastSentUtc, 0)), 0), " +
        "LastReceivedUtc = NULLIF(MAX(COALESCE(LastReceivedUtc, 0), COALESCE(excluded.LastReceivedUtc, 0)), 0), " +
        "Address = excluded.Address, " +
        "DisplayName = CASE WHEN excluded.DisplayName IS NOT NULL AND excluded.DisplayName <> '' AND UPPER(excluded.DisplayName) <> excluded.NormalizedAddress " +
        "THEN excluded.DisplayName ELSE DisplayName END";

    public RecipientHistoryService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public Task RecordReceivedAsync(Guid accountId, string address, string displayName, DateTime whenUtc)
    {
        if (RecipientAddressHeuristics.IsAutomatedAddress(address, displayName))
            return Task.CompletedTask;

        return Connection.ExecuteAsync(UpsertSql, CreateUpsertArguments(accountId, address, displayName, sent: false, whenUtc));
    }

    public Task RecordSentAsync(Guid accountId, IReadOnlyCollection<RecipientAddress> recipients, DateTime whenUtc)
    {
        var valid = recipients?
            .Where(recipient => IsStructurallyValid(recipient.Address))
            .GroupBy(recipient => ContactEmailAddress.Normalize(recipient.Address), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList() ?? [];

        if (valid.Count == 0)
            return Task.CompletedTask;

        return Connection.RunInTransactionAsync(connection =>
        {
            foreach (var recipient in valid)
                connection.Execute(UpsertSql, CreateUpsertArguments(accountId, recipient.Address, recipient.DisplayName, sent: true, whenUtc));
        });
    }

    public Task SuppressAsync(Guid accountId, string address)
    {
        if (!IsStructurallyValid(address))
            return Task.CompletedTask;

        return Connection.ExecuteAsync(
            "INSERT INTO RecipientHistory (Id, AccountId, NormalizedAddress, Address, DisplayName, SentCount, ReceivedCount, LastSentUtc, LastReceivedUtc, IsSuppressed) " +
            "VALUES (?, ?, ?, ?, NULL, 0, 0, NULL, NULL, 1) " +
            "ON CONFLICT(AccountId, NormalizedAddress) DO UPDATE SET IsSuppressed = 1",
            Guid.NewGuid(),
            accountId,
            ContactEmailAddress.Normalize(address),
            address.Trim());
    }

    public Task<List<RecipientHistory>> SearchAsync(Guid accountId, string query, int limit = 30)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Task.FromResult(new List<RecipientHistory>());

        var pattern = $"%{EscapeLike(trimmed)}%";
        return Connection.QueryAsync<RecipientHistory>(
            "SELECT * FROM RecipientHistory " +
            "WHERE AccountId = ? AND IsSuppressed = 0 " +
            "AND (Address LIKE ? ESCAPE '\\' OR DisplayName LIKE ? ESCAPE '\\') " +
            "ORDER BY SentCount * 3 + ReceivedCount DESC LIMIT ?",
            accountId,
            pattern,
            pattern,
            Math.Max(1, limit));
    }

    public Task ClearAsync(Guid accountId)
        => Connection.ExecuteAsync("DELETE FROM RecipientHistory WHERE AccountId = ?", accountId);

    private static object[] CreateUpsertArguments(Guid accountId, string address, string displayName, bool sent, DateTime whenUtc)
    {
        var trimmedAddress = address.Trim();
        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

        return
        [
            Guid.NewGuid(),
            accountId,
            ContactEmailAddress.Normalize(trimmedAddress),
            trimmedAddress,
            name,
            sent ? 1 : 0,
            sent ? 0 : 1,
            sent ? whenUtc : null,
            sent ? null : whenUtc
        ];
    }

    private static bool IsStructurallyValid(string address)
    {
        var trimmed = address?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        var at = trimmed.LastIndexOf('@');
        return at > 0 && at < trimmed.Length - 1;
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
