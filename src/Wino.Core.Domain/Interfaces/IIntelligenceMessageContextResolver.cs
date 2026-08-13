#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Core.Domain.Interfaces;

public interface IIntelligenceMessageContextResolver
{
    Task<SemanticIndexAvailableRange?> GetAvailableRangeAsync(
        Guid localAccountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IntelligenceMessageCandidate>> GetCandidatesAsync(
        Guid localAccountId,
        DateTimeOffset? cutoffUtc = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IntelligenceMessageCandidate>> GetCandidatesAsync(
        Guid localAccountId,
        DateTimeOffset? cutoffUtc,
        DateTimeOffset? throughUtcExclusive,
        CancellationToken cancellationToken = default);

    Task<IntelligenceMessageCandidate?> FindCandidateAsync(
        Guid localAccountId,
        string messageId,
        CancellationToken cancellationToken = default);

    Task<SemanticMailContent> GetContentAsync(
        Guid localAccountId,
        IntelligenceMessageCandidate candidate,
        CancellationToken cancellationToken = default);
}
