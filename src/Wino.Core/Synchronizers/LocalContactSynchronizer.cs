using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers;

/// <summary>
/// Completes contact requests that were already applied to an account-local address book.
/// It intentionally owns no HTTP client and performs no network operation.
/// </summary>
public sealed class LocalContactSynchronizer
{
    public Task ExecuteRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<ContactSynchronizationResult> SynchronizeAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ContactSynchronizationResult.Empty);
    }
}
