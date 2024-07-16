using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IWinoServerConnectionManager
    {
        WinoServerConnectionStatus Status { get; }
        Task<bool> ConnectAsync();
        Task<bool> DisconnectAsync();
    }

    public interface IWinoServerConnectionManager<TAppServiceConnection> : IWinoServerConnectionManager, IInitializeAsync
    {
        TAppServiceConnection Connection { get; set; }
    }
}
