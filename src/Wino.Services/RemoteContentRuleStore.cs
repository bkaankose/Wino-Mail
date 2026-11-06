using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;
using Wino.Messaging.UI;

namespace Wino.Services;

public class RemoteContentRuleStore : BaseDatabaseService, IRemoteContentRuleStore
{
    public RemoteContentRuleStore(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public Task<List<RemoteContentSenderRule>> GetAllAsync()
        => Connection.Table<RemoteContentSenderRule>().OrderByDescending(a => a.CreatedUtc).ToListAsync();

    public Task<int> GetCountAsync()
        => Connection.Table<RemoteContentSenderRule>().CountAsync();

    public async Task<RemoteContentSenderRule?> GetByPatternAsync(string pattern)
    {
        var normalized = RemoteContentRulePattern.Normalize(pattern, out _);
        if (normalized == null)
            return null;

        return await Connection.Table<RemoteContentSenderRule>()
            .Where(a => a.Pattern == normalized)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    public async Task<RemoteContentSenderRule?> FindAsync(string address)
    {
        var normalizedAddress = RemoteContentRulePattern.Normalize(address, out _);
        if (normalizedAddress == null)
            return null;

        var candidates = new List<string> { normalizedAddress };
        candidates.AddRange(RemoteContentRulePattern.GetDomainCandidates(RemoteContentRulePattern.GetDomain(normalizedAddress)));

        var placeholders = string.Join(", ", candidates.Select(_ => "?"));
        var matches = await Connection.QueryAsync<RemoteContentSenderRule>(
            $"SELECT * FROM RemoteContentSenderRule WHERE Pattern IN ({placeholders})",
            candidates.Cast<object>().ToArray()).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            var match = matches.FirstOrDefault(a => a.Pattern == candidate);
            if (match != null)
                return match;
        }

        return null;
    }

    public async Task UpsertAsync(RemoteContentSenderRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var normalized = RemoteContentRulePattern.Normalize(rule.Pattern, out var kind)
            ?? throw new ArgumentException($"'{rule.Pattern}' is not an email address or a domain.", nameof(rule));

        rule.Pattern = normalized;
        rule.Kind = kind;

        if (rule.CreatedUtc == default)
            rule.CreatedUtc = DateTime.UtcNow;

        var existing = await Connection.Table<RemoteContentSenderRule>()
            .Where(a => a.Pattern == normalized)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (existing != null && existing.Id != rule.Id)
            rule.Id = existing.Id;

        await Connection.InsertOrReplaceAsync(rule, typeof(RemoteContentSenderRule)).ConfigureAwait(false);

        Messenger.Send(new RemoteContentRulesChangedMessage(normalized));
    }

    public async Task DeleteAsync(Guid id)
    {
        var existing = await Connection.FindAsync<RemoteContentSenderRule>(id).ConfigureAwait(false);
        if (existing == null)
            return;

        await Connection.DeleteAsync<RemoteContentSenderRule>(id).ConfigureAwait(false);

        Messenger.Send(new RemoteContentRulesChangedMessage(existing.Pattern));
    }

    public Task TouchAsync(Guid id, DateTime whenUtc)
        => Connection.ExecuteAsync("UPDATE RemoteContentSenderRule SET LastAppliedUtc = ? WHERE Id = ?", whenUtc, id);
}
