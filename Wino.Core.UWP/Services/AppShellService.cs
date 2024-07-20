#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#else
using Microsoft.UI.Xaml;
#endif

namespace Wino.Core.WinUI.Services
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
