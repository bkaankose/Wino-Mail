using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public sealed record IntelligenceAccountEligibility(
    Guid AccountId,
    string AccountName,
    bool IsEligible,
    IntelligenceBackendKind BackendKind,
    string Reason);

public sealed record IntelligenceSearchEligibilityResult(IReadOnlyList<IntelligenceAccountEligibility> Accounts)
{
    public bool HasEligibleAccounts => Accounts.Any(account => account.IsEligible);

    public bool HasCompatibleBackends => Accounts
        .Where(account => account.IsEligible)
        .Select(account => account.BackendKind)
        .Distinct()
        .Take(2)
        .Count() <= 1;
}

public interface IIntelligenceSearchEligibilityService
{
    Task<IntelligenceSearchEligibilityResult> ResolveAsync(
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken cancellationToken = default);
}
