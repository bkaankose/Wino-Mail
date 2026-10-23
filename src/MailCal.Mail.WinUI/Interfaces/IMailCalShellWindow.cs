using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailCal.Mail.WinUI.Interfaces;

public interface IMailCalShellWindow
{
    void HandleAppActivation(string? launchArguments, string? tileId = null, string? appId = null);
    TitleBar GetTitleBar();
    Frame GetMainFrame();
    FrameworkElement GetRootContent();
}
