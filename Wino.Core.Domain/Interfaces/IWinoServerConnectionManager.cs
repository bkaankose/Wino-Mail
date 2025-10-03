using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Simple wrapper class to maintain compatibility with the original WinoServerResponse structure.
/// </summary>
/// <typeparam name="T">Type of the expected response.</typeparam>
public class WinoServerResponse<T>
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; }
    public T Data { get; set; }

    public static WinoServerResponse<T> CreateSuccessResponse(T data)
    {
        return new WinoServerResponse<T>
        {
            IsSuccess = true,
            Data = data
        };
    }

    public static WinoServerResponse<T> CreateErrorResponse(string message)
    {
        return new WinoServerResponse<T>
        {
            IsSuccess = false,
            Message = message
        };
    }

    public void ThrowIfFailed()
    {
        if (!IsSuccess)
            throw new InvalidOperationException(Message);
    }
}

/// <summary>
/// Connection status enum to maintain compatibility.
/// </summary>
public enum WinoServerConnectionStatus
{
    None,
    Connecting,
    Connected,
    Disconnected,
    Failed
}

public interface IWinoServerConnectionManager
{
    /// <summary>
    /// When the connection status changes, this event will be triggered.
    /// </summary>
    event EventHandler<WinoServerConnectionStatus> StatusChanged;

    /// <summary>
    /// Gets the connection status.
    /// </summary>
    WinoServerConnectionStatus Status { get; }

    /// <summary>
    /// Launches Full Trust process (Wino Server) and awaits connection completion.
    /// If connection is not established in 10 seconds, it will return false.
    /// If the server process is already running, it'll connect to existing one.
    /// If the server process is not running, it'll be launched and connection establishment is awaited.
    /// </summary>
    /// <returns>Whether connection is established or not.</returns>
    Task<bool> ConnectAsync();

    /// <summary>
    /// Queues a new user request to be processed by Wino Server.
    /// Healthy connection must present before calling this method.
    /// </summary>
    /// <param name="request">Request to queue for synchronizer in the server.</param>
    /// <param name="accountId">Account id to queueu request for.</param>
    Task QueueRequestAsync(IRequestBase request, Guid accountId);

    /// <summary>
    /// Returns response from server for the given request.
    /// </summary>
    /// <typeparam name="TResponse">Response type.</typeparam>
    /// <typeparam name="TRequestType">Request type.</typeparam>
    /// <param name="clientMessage">Request type.</param>
    /// <returns>Response received from the server for the given TResponse type.</returns>
    Task<WinoServerResponse<TResponse>> GetResponseAsync<TResponse, TRequestType>(TRequestType clientMessage, CancellationToken cancellationToken = default) where TRequestType : IClientMessage;

    /// <summary>
    /// Handle for connecting to the server.
    /// If the server is already running, it'll connect to existing one.
    /// Callers can await this handle to wait for connection establishment.
    /// </summary>
    TaskCompletionSource<bool> ConnectingHandle { get; }
}

public interface IWinoServerConnectionManager<TAppServiceConnection> : IWinoServerConnectionManager, IInitializeAsync
{
    /// <summary>
    /// Existing connection handle to the server of TAppServiceConnection type.
    /// </summary>
    TAppServiceConnection Connection { get; set; }
}
