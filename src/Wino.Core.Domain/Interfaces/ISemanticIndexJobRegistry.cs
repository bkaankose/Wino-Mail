using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface ISemanticIndexJobRegistry
{
    bool TryStart(Guid accountId, Func<CancellationToken, Task> worker, out Task task);
    bool IsRunning(Guid accountId);
    Task CancelAndWaitAsync(Guid accountId);
}
