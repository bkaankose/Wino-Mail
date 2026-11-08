using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Sender rules for remote content. Every write publishes <c>RemoteContentRulesChangedMessage</c>.
/// </summary>
public interface IRemoteContentRuleStore
{
    Task<List<RemoteContentSenderRule>> GetAllAsync();

    Task<int> GetCountAsync();

    /// <summary>
    /// The rule for an exact pattern after normalization, or null.
    /// </summary>
    Task<RemoteContentSenderRule?> GetByPatternAsync(string pattern);

    /// <summary>
    /// The rule that applies to a sender address: the exact address first, then its domain and each parent domain.
    /// </summary>
    Task<RemoteContentSenderRule?> FindAsync(string address);

    /// <summary>
    /// Normalizes the pattern and saves the rule. A rule with the same pattern is replaced in place and keeps its id.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern is not an address or a domain.</exception>
    Task UpsertAsync(RemoteContentSenderRule rule);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// Records when a rule last decided a message. Does not publish a change.
    /// </summary>
    Task TouchAsync(Guid id, DateTime whenUtc);
}
