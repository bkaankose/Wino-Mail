#if NET8_0
using Microsoft.UI.Xaml;
#else
using Microsoft.UI.Xaml;
#endif

namespace Wino.Shared.WinRT.Services
{
    public interface IAppShellService
    {
        Window AppWindow { get; set; }
    }

    public class AppShellService : IAppShellService
    {
        public Window AppWindow { get; set; }
    }
}
