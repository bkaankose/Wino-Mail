using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Views;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class MigrationWindow : WindowEx, IWinoFrameProvider
{
    private bool _allowClose;
    private bool _isPromptOpen;

    public Frame GetRootFrame() => RootFrame;
    public Frame? GetFrame(NavigationReferenceFrame frameType) =>
        frameType == NavigationReferenceFrame.ShellFrame ? RootFrame : null;

    public MigrationWindow()
    {
        InitializeComponent();
        MinWidth = 760;
        MinHeight = 620;
        Width = 980;
        Height = 720;
        Title = Wino.Core.Domain.Translator.MigrationWindow_Title;
        this.SetIcon("Assets/Wino_Icon.ico");
        this.CenterOnScreen();

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
    }

    public void AllowClose() => _allowClose = true;

    private async void OnAppWindowClosing(object sender, AppWindowClosingEventArgs e)
    {
        if (_allowClose || (Application.Current as App)?.IsExiting == true)
            return;

        e.Cancel = true;
        if (_isPromptOpen || RootFrame.Content is not MigrationPage page)
            return;

        _isPromptOpen = true;
        try
        {
            if (await page.ConfirmExitAsync())
                (Application.Current as App)?.ExitApplication();
        }
        finally
        {
            _isPromptOpen = false;
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        Closed -= OnWindowClosed;
        AppWindow.Closing -= OnAppWindowClosing;
        WindowCleanupHelper.CleanupFrame(RootFrame);
    }
}
