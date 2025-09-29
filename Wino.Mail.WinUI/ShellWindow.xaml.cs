using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.WinUI.Interfaces;
using Wino.Views;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class ShellWindow : WindowEx, IWinoShellWindow
{
    public ShellWindow()
    {
        InitializeComponent();

        ConfigureTitleBar();
    }

    private void ConfigureTitleBar()
    {
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
    }

    public void HandleAppActivation(LaunchActivatedEventArgs args)
    {
        // TODO: Handle protocol activations.

        MainShellFrame.Navigate(typeof(AppShell));
    }

    public Microsoft.UI.Xaml.Controls.TitleBar GetTitleBar() => ShellTitleBar;

    public Frame GetMainFrame() => MainShellFrame;
}
