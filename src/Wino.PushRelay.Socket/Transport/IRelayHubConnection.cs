#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.PushRelay.Socket.Transport;

/// <summary>
/// The hub connection as the client uses it: one connection per connect attempt, disposed after it closes. The
/// SignalR implementation is <see cref="SignalRRelayHubConnection"/>; tests use an in-memory fake.
/// </summary>
public interface IRelayHubConnection : IAsyncDisposable
{
    /// <summary>The relay problem of the last refused HTTP step (negotiate), when its body was a problem document.</summary>
    PushRelayProblem? LastProblem { get; }

    void OnHello(Func<RelaySocketHello, Task> handler);
    void OnEvent(Func<Dictionary<string, string>, Task> handler);
    void OnBye(Func<string, Task> handler);

    /// <summary>Raised once when the connection closes for any reason other than <see cref="DisposeAsync"/> before start.</summary>
    event Func<Exception?, Task>? Closed;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<int> AckAsync(string[] eventIds, CancellationToken cancellationToken);
}

/// <summary>What a connection needs: where to connect, which device it is and how to get the bearer token.</summary>
public sealed record RelayHubConnectionSettings(Uri HubUri, Guid DeviceId, Func<CancellationToken, Task<string>> AccessTokenProvider);

public interface IRelayHubConnectionFactory
{
    IRelayHubConnection Create(RelayHubConnectionSettings settings);
}
