using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class IntelligenceSearchEligibilityService(
    IAccountService accountService,
    IIntelligenceBackend intelligenceBackend,
    IWinoIntelligenceEntitlementService? entitlementService = null) : IIntelligenceSearchEligibilityService
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
            var entitled = entitlementService?.Current.CanAccessSurfaces ?? true;
            var enabled = entitled && account.Preferences?.IsSemanticIndexingEnabled == true;
            results.Add(new(
                accountId,
                account.Name,
                enabled,
                intelligenceBackend.Kind,
                enabled ? string.Empty : entitled
                    ? "Semantic indexing is disabled"
                    : "An active Wino Intelligence subscription is required"));
        }

        return new(results);
    }
}
