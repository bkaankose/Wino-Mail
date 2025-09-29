using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Core.WinUI.Interfaces;

public interface IWinoShellWindow
{
    void HandleAppActivation(LaunchActivatedEventArgs args);
    TitleBar GetTitleBar();
    Frame GetMainFrame();
    FrameworkElement GetRootContent();
}
