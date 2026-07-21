using CommunityToolkit.AppServices;

namespace Wino.AppServices.Contracts;

// The Labs generator supplies the modern .NET UWP application entry point. Wino's
// actual IPC is the native duplex AppService connection owned by the messenger bridge.
[AppService(AppServiceProtocol.ServiceName)]
public interface ICompanionAppService
{
    Task PingAsync();
}
