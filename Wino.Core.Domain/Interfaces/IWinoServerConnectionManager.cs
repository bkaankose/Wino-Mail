using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Server;

namespace Wino.Core.Domain.Interfaces
{
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
        /// If connection is not established in 5 seconds, it will return false.
        /// If the server process is already running, it'll connect to existing one.
        /// If the server process is not running, it'll be launched and connection establishment is awaited.
        /// </summary>
        /// <returns>Whether connection is established or not.</returns>
        Task<bool> ConnectAsync();

        /// <summary>
        /// Disconnects from existing connection and disposes the connection.
        /// </summary>
        /// <returns>Whether disconnection is succesfull or not.</returns>
        Task<bool> DisconnectAsync();

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
        Task<WinoServerResponse<TResponse>> GetResponseAsync<TResponse, TRequestType>(TRequestType clientMessage) where TRequestType : IClientMessage;
    }

    public interface IWinoServerConnectionManager<TAppServiceConnection> : IWinoServerConnectionManager, IInitializeAsync
    {
        /// <summary>
        /// Existing connection handle to the server of TAppServiceConnection type.
        /// </summary>
        TAppServiceConnection Connection { get; set; }
    }
}
