using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Rules;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Reads and writes an account's server-side inbox rules (currently Exchange only). The server is the
/// source of truth: rules are fetched on demand and written immediately, with no local persistence.
/// Resolves the account's synchronizer via <see cref="ISynchronizerFactory"/> and delegates to it.
/// </summary>
public interface IRuleService
{
    /// <summary>Whether the account exposes server-side inbox rules. Cheap: no network or synchronizer resolution.</summary>
    bool SupportsRules(MailAccount account);

    Task<IReadOnlyList<RemoteInboxRule>> GetRulesAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the rule when its <see cref="RemoteInboxRule.Id"/> is empty, otherwise updates it.
    /// When the result reports <see cref="InboxRuleUpdateResult.RequiresOutlookRuleBlobRemoval"/>, retry
    /// with <paramref name="removeOutlookRuleBlob"/> = true after user consent.
    /// </summary>
    Task<InboxRuleUpdateResult> SaveRuleAsync(Guid accountId, RemoteInboxRule rule, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default);

    Task<InboxRuleUpdateResult> DeleteRuleAsync(Guid accountId, string ruleId, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new ordering. <paramref name="orderedRules"/> is the full set in the desired order;
    /// editable rules whose priority changed are written back (read-only rules keep their server priority).
    /// </summary>
    Task<InboxRuleUpdateResult> ReorderRulesAsync(Guid accountId, IReadOnlyList<RemoteInboxRule> orderedRules, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default);
}
