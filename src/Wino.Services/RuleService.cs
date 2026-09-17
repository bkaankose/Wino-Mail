using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Rules;

namespace Wino.Services;

/// <summary>
/// Thin orchestrator over the account's synchronizer for server-side inbox rules. Holds no state and
/// no local cache; every call hits the server through <see cref="IWinoSynchronizerBase"/>.
/// </summary>
public class RuleService : IRuleService
{
    private readonly ISynchronizerFactory _synchronizerFactory;
    private readonly ILogger _logger = Log.ForContext<RuleService>();

    public RuleService(ISynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    public bool SupportsRules(MailAccount account) => account?.ProviderType == MailProviderType.Exchange;

    public async Task<IReadOnlyList<RemoteInboxRule>> GetRulesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var synchronizer = await GetRuleCapableSynchronizerAsync(accountId).ConfigureAwait(false);
        return await synchronizer.GetInboxRulesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<InboxRuleUpdateResult> SaveRuleAsync(Guid accountId, RemoteInboxRule rule, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default)
    {
        if (rule == null)
            return Task.FromResult(InboxRuleUpdateResult.Failed(Translator.Rules_NoRuleSupplied));

        if (rule.IsReadOnly)
            return Task.FromResult(InboxRuleUpdateResult.Failed(Translator.Rules_ReadOnlyTooltip));

        var change = string.IsNullOrEmpty(rule.Id) ? InboxRuleChange.Create(rule) : InboxRuleChange.Update(rule);
        return ApplyAsync(accountId, new[] { change }, removeOutlookRuleBlob, cancellationToken);
    }

    public Task<InboxRuleUpdateResult> DeleteRuleAsync(Guid accountId, string ruleId, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ruleId))
            return Task.FromResult(InboxRuleUpdateResult.Failed(Translator.Rules_NoRuleSupplied));

        return ApplyAsync(accountId, new[] { InboxRuleChange.Delete(ruleId) }, removeOutlookRuleBlob, cancellationToken);
    }

    public Task<InboxRuleUpdateResult> ReorderRulesAsync(Guid accountId, IReadOnlyList<RemoteInboxRule> orderedRules, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default)
    {
        if (orderedRules == null || orderedRules.Count == 0)
            return Task.FromResult(InboxRuleUpdateResult.Ok);

        // Assign 1-based priorities by position; write back only editable rules whose priority changed.
        // Read-only rules keep their server priority (we never Set them).
        var changes = new List<InboxRuleChange>();
        for (var i = 0; i < orderedRules.Count; i++)
        {
            var rule = orderedRules[i];
            var newPriority = i + 1;

            if (rule.IsReadOnly || rule.Priority == newPriority)
                continue;

            rule.Priority = newPriority;
            changes.Add(InboxRuleChange.Update(rule));
        }

        if (changes.Count == 0)
            return Task.FromResult(InboxRuleUpdateResult.Ok);

        return ApplyAsync(accountId, changes, removeOutlookRuleBlob, cancellationToken);
    }

    private async Task<InboxRuleUpdateResult> ApplyAsync(Guid accountId, IReadOnlyList<InboxRuleChange> changes, bool removeOutlookRuleBlob, CancellationToken cancellationToken)
    {
        try
        {
            var synchronizer = await GetRuleCapableSynchronizerAsync(accountId).ConfigureAwait(false);
            return await synchronizer.UpdateInboxRulesAsync(changes, removeOutlookRuleBlob, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update inbox rules for account {AccountId}.", accountId);
            return InboxRuleUpdateResult.Failed(ex.Message);
        }
    }

    private async Task<IWinoSynchronizerBase> GetRuleCapableSynchronizerAsync(Guid accountId)
    {
        var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(accountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No synchronizer is available for account {accountId}.");

        if (!synchronizer.SupportsInboxRules)
            throw new NotSupportedException(Translator.Rules_ExchangeOnly);

        return synchronizer;
    }
}
