using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Interfaces;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class WelcomeWindow : WindowEx
{
    private bool _allowClose;

    public Frame GetRootFrame() => RootFrame;

    public WelcomeWindow()
    {
        InitializeComponent();

        MinWidth = 980;
        MinHeight = 900;
        Title = "Wino Mail";
        this.SetIcon("Assets/Wino_Icon.ico");

        ConfigureWindowChrome();
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void ConfigureWindowChrome()
    {
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;

        Width = 980;
        Height = 720;

        this.CenterOnScreen();

        var themeService = WinoApplication.Current.Services.GetService<INewThemeService>();
        themeService?.UpdateSystemCaptionButtonColors();
    }

    private void OnAppWindowClosing(object sender, AppWindowClosingEventArgs e)
    {
        if (_allowClose || (Application.Current as App)?.IsExiting == true)
            return;

        e.Cancel = true;

        var windowManager = WinoApplication.Current.Services.GetService<IWinoWindowManager>();
        windowManager?.HideWindow(this);
    }

    public void AllowClose()
    {
        _allowClose = true;
    }
}
