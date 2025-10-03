using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.WinUI.Services;

/// <summary>
/// Empty implementation of IWinoServerConnectionManager that returns default values.
/// This replaces the old AppServiceConnection-based implementation.
/// </summary>
public class EmptyWinoServerConnectionManager : IWinoServerConnectionManager
{
    public event EventHandler<WinoServerConnectionStatus> StatusChanged { add { } remove { } }

    public WinoServerConnectionStatus Status => WinoServerConnectionStatus.Connected;

    public TaskCompletionSource<bool> ConnectingHandle { get; } = new TaskCompletionSource<bool>();

    public EmptyWinoServerConnectionManager()
    {
        ConnectingHandle.SetResult(true);
    }

    public Task<bool> ConnectAsync()
    {
        return Task.FromResult(true);
    }

    public Task QueueRequestAsync(IRequestBase request, Guid accountId)
    {
        return Task.CompletedTask;
    }

    public Task<WinoServerResponse<TResponse>> GetResponseAsync<TResponse, TRequestType>(TRequestType clientMessage, CancellationToken cancellationToken = default) 
        where TRequestType : IClientMessage
    {
        var response = WinoServerResponse<TResponse>.CreateSuccessResponse(default(TResponse));
        return Task.FromResult(response);
    }
}

/// <summary>
/// Generic empty implementation for typed connection managers.
/// </summary>
/// <typeparam name="TAppServiceConnection">The connection type (not used in this implementation)</typeparam>
public class EmptyWinoServerConnectionManager<TAppServiceConnection> : EmptyWinoServerConnectionManager, IWinoServerConnectionManager<TAppServiceConnection>
{
    public TAppServiceConnection Connection { get; set; }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }
}