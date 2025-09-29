using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Core.WinUI.Interfaces;

public interface IWinoShellWindow
{
    void HandleAppActivation(LaunchActivatedEventArgs args);
    Microsoft.UI.Xaml.Controls.TitleBar GetTitleBar();
    Frame GetMainFrame();
}
