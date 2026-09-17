using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class JunkSenderService : BaseDatabaseService, IJunkSenderService
{
    public JunkSenderService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public async Task<IReadOnlyList<JunkSender>> GetSendersAsync(Guid accountId, JunkListType listType)
        => await Connection.Table<JunkSender>()
            .Where(sender => sender.AccountId == accountId && sender.ListType == listType)
            .OrderBy(sender => sender.Address)
            .ToListAsync()
            .ConfigureAwait(false);

    public async Task AddSenderAsync(Guid accountId, string address, JunkListType listType)
    {
        var normalized = Normalize(address);
        if (string.IsNullOrEmpty(normalized))
            return;

        // A sender is on one list at a time: drop it from the opposite list first (Outlook behavior).
        var opposite = listType == JunkListType.Blocked ? JunkListType.Safe : JunkListType.Blocked;
        await Connection.Table<JunkSender>()
            .DeleteAsync(sender => sender.AccountId == accountId && sender.Address == normalized && sender.ListType == opposite)
            .ConfigureAwait(false);

        var existing = await Connection.Table<JunkSender>()
            .FirstOrDefaultAsync(sender => sender.AccountId == accountId && sender.Address == normalized && sender.ListType == listType)
            .ConfigureAwait(false);

        if (existing != null)
            return;

        await Connection.InsertAsync(new JunkSender
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Address = normalized,
            ListType = listType,
            CreatedAtUtc = DateTime.UtcNow,
        }, typeof(JunkSender)).ConfigureAwait(false);
    }

    public async Task RemoveSenderAsync(Guid accountId, string address, JunkListType listType)
    {
        var normalized = Normalize(address);
        if (string.IsNullOrEmpty(normalized))
            return;

        await Connection.Table<JunkSender>()
            .DeleteAsync(sender => sender.AccountId == accountId && sender.Address == normalized && sender.ListType == listType)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsListedAsync(Guid accountId, string address, JunkListType listType)
    {
        var normalized = Normalize(address);
        if (string.IsNullOrEmpty(normalized))
            return false;

        var match = await Connection.Table<JunkSender>()
            .FirstOrDefaultAsync(sender => sender.AccountId == accountId && sender.Address == normalized && sender.ListType == listType)
            .ConfigureAwait(false);

        return match != null;
    }

    public async Task<int> ImportAsync(Guid accountId, JunkListType listType, IEnumerable<string> addresses)
    {
        var added = 0;

        foreach (var address in addresses ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(address))
                continue;

            var alreadyListed = await IsListedAsync(accountId, address, listType).ConfigureAwait(false);
            await AddSenderAsync(accountId, address, listType).ConfigureAwait(false);

            if (!alreadyListed)
                added++;
        }

        return added;
    }

    private static string Normalize(string address) => address?.Trim().ToLowerInvariant() ?? string.Empty;
}
