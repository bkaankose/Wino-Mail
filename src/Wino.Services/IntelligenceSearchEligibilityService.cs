using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class IntelligenceSearchEligibilityService(
    IAccountService accountService,
    IIntelligenceBackend intelligenceBackend) : IIntelligenceSearchEligibilityService
{
    public async Task<IntelligenceSearchEligibilityResult> ResolveAsync(
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken cancellationToken = default)
    {
        var results = new List<IntelligenceAccountEligibility>();
        foreach (var accountId in accountIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = await accountService.GetAccountAsync(accountId).ConfigureAwait(false);
            if (account is null)
                continue;
            var enabled = account.Preferences?.IsSemanticIndexingEnabled == true;
            results.Add(new(
                accountId,
                account.Name,
                enabled,
                intelligenceBackend.Kind,
                enabled ? string.Empty : "Semantic indexing is disabled"));
        }

        return new(results);
    }
}
