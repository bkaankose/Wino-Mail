using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Core.Domain.Interfaces;

public interface ISemanticIndexCoordinator
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<SemanticIndexPlan> CalculatePlanAsync(Guid localMailAccountId, SemanticIndexRangePreset preset, bool automaticallyIndexNewMessages, CancellationToken cancellationToken = default);
    Task<SemanticIndexPlan> CalculatePlanAsync(Guid localMailAccountId, DateTimeOffset cutoffUtc, bool automaticallyIndexNewMessages, CancellationToken cancellationToken = default);
    Task<SemanticIndexPlan> CalculatePlanAsync(Guid localMailAccountId, DateTimeOffset cutoffUtc, DateTimeOffset throughUtcExclusive, bool automaticallyIndexNewMessages, CancellationToken cancellationToken = default);
    Task<SemanticIndexAvailableRange?> GetAvailableRangeAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task StartIndexingAsync(Guid localMailAccountId, SemanticIndexPlan plan, CancellationToken cancellationToken = default);
    Task IndexMessageAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default);
    Task<SemanticIndexAccountState> DownloadAvailableIntelligenceAsync(Guid localMailAccountId, IProgress<SemanticIndexingProgress>? progress = null, CancellationToken cancellationToken = default);
    SemanticIndexJobSnapshot GetJobSnapshot(Guid localMailAccountId);
    Task<SemanticMessageIndexState> GetMessageStateAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default);
    Task<SemanticIndexAccountState> GetStateAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task EnsureMailboxAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task DeleteIndexAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task DeleteLocalIndexAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
}
