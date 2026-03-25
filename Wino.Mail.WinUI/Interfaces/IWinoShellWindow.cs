using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.WinUI.Interfaces;

public interface IWinoShellWindow
{
    void HandleAppActivation(string? launchArguments, string? tileId = null, string? appId = null);
    TitleBar GetTitleBar();
    Frame GetMainFrame();
    FrameworkElement GetRootContent();
}
