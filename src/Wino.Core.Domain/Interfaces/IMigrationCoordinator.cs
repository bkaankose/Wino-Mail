using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Migration;

namespace Wino.Core.Domain.Interfaces;

public interface IMigrationCoordinator
{
    event EventHandler<MigrationProgress> ProgressChanged;

    Task<MigrationPlan> InspectAsync(CancellationToken cancellationToken = default);

    Task<MigrationResult> RunAsync(
        IReadOnlyList<MigrationAccountOptions> accountOptions,
        CancellationToken cancellationToken = default);

    Task<MigrationResult> StartFreshAsync(CancellationToken cancellationToken = default);

    Task MarkAccountAuthorizationResolvedAsync(
        Guid accountId,
        bool wasSkipped,
        CancellationToken cancellationToken = default);
}

public interface IMigrationStep
{
    MigrationStepKind Kind { get; }
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}

public interface IMigrationClock
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}
