using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IBackgroundServiceConnection
{
    event Action? Reconnected;

    bool IsConnected { get; }

    Task EnsureConnectedAsync(CancellationToken cancellationToken = default);
}
