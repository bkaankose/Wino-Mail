using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI.Interfaces;

public interface IWinoShellWindow : IRecipient<TitleBarShellContentUpdated>
{
    void HandleAppActivation(LaunchActivatedEventArgs args);
    TitleBar GetTitleBar();
    Frame GetMainFrame();
    FrameworkElement GetRootContent();
}
