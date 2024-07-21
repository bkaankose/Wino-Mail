using System;
using System.Threading.Tasks;
using Wino.Domain.Enums;

namespace Wino.Domain.Interfaces
{
    public interface IWinoServerConnectionManager
    {
        Task<bool> ConnectAsync();
        Task<bool> DisconnectAsync();

        WinoServerConnectionStatus Status { get; }
        event EventHandler<WinoServerConnectionStatus> StatusChanged;
        void DisposeConnection();

        void QueueRequest(IRequestBase request, Guid accountId);
    }

    public interface IWinoServerConnectionManager<TAppServiceConnection> : IWinoServerConnectionManager, IInitializeAsync
    {
        TAppServiceConnection Connection { get; set; }
    }
}
