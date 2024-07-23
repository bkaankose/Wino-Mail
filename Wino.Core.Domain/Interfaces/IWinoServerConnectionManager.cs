using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Server;

namespace Wino.Core.Domain.Interfaces
{
    public interface IWinoServerConnectionManager
    {
        event EventHandler<WinoServerConnectionStatus> StatusChanged;
        WinoServerConnectionStatus Status { get; }

        Task<bool> ConnectAsync();
        Task<bool> DisconnectAsync();

        void DisposeConnection();

        Task QueueRequestAsync(IRequestBase request, Guid accountId);

        Task<WinoServerResponse<TResponse>> GetResponseAsync<TResponse, TRequestType>(TRequestType clientMessage) where TRequestType : IClientMessage;
    }

    public interface IWinoServerConnectionManager<TAppServiceConnection> : IWinoServerConnectionManager, IInitializeAsync
    {
        TAppServiceConnection Connection { get; set; }
    }
}
