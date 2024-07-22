using System;
using System.Threading.Tasks;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;

namespace Wino.Shared.WinRT.Services
{
    public abstract class ServerConnectionManagerBase : IWinoServerConnectionManager
    {
        public event EventHandler<WinoServerConnectionStatus> StatusChanged;

        private WinoServerConnectionStatus status;

        public WinoServerConnectionStatus Status
        {
            get { return status; }
            private set
            {
                status = value;
                StatusChanged?.Invoke(this, value);
            }
        }

        public virtual Task<bool> ConnectAsync() => throw new NotImplementedException();

        public Task<bool> DisconnectAsync() => throw new NotImplementedException();

        public void DisposeConnection() => throw new NotImplementedException();

        public void QueueRequest(IRequestBase request, Guid accountId) => throw new NotImplementedException();
    }
}
