using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class WelcomeWindow : WindowEx, IWinoFrameProvider
{
    private bool _allowClose;
    private bool _isPreparedForClose;

    public Frame GetRootFrame() => RootFrame;

    public Frame? GetFrame(NavigationReferenceFrame frameType)
        => frameType == NavigationReferenceFrame.ShellFrame ? RootFrame : null;

    public WelcomeWindow()
    {
        InitializeComponent();

        MinWidth = 980;
        MinHeight = 900;
        Title = Wino.NotificationHost.Contracts.ReleaseIdentity.Current.DisplayNames["Mail"];
        this.SetIcon("Assets/Wino_Icon.ico");

        ConfigureWindowChrome();

        IsResizable = false;
        IsMaximizable = false;

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
    }

    private void ConfigureWindowChrome()
    {
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;

        Width = 980;
        Height = 720;

        this.CenterOnScreen();

        // The setup flow is laid out for this one size; resizing or maximizing only adds empty space.
        // Set on the presenter after the title bar is extended, which otherwise restores the caption defaults.
        //if (AppWindow.Presenter is OverlappedPresenter presenter)
        //{
        //    presenter.IsResizable = false;
        //    presenter.IsMaximizable = false;
        //}

        var underlyingThemeService = WinoApplication.Current.Services.GetService<IUnderlyingThemeService>();
        if (underlyingThemeService != null)
        {
            SystemCaptionButtonColorHelper.Apply(
                AppWindow.TitleBar,
                underlyingThemeService.IsUnderlyingThemeDark());
        }
    }

    private void OnAppWindowClosing(object sender, AppWindowClosingEventArgs e)
    {
        var app = Application.Current as App;
        if (_allowClose || app?.IsExiting == true)
            return;

        // Keep this window alive while the app shuts down. Letting the last XAML window close here
        // ends the dispatcher before the exit sequence (tray companion, Application.Exit) can finish.
        if (app?.TryExitApplicationOnWelcomeWindowClose(this) == true)
            e.Cancel = true;
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    public void PrepareForClose()
    {
        if (_isPreparedForClose)
            return;

        _isPreparedForClose = true;
        WindowCleanupHelper.CleanupFrame(RootFrame);
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        Closed -= OnWindowClosed;
        AppWindow.Closing -= OnAppWindowClosing;
        PrepareForClose();
    }
}
