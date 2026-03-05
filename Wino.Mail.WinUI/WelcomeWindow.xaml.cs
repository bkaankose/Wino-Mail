using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class WelcomeWindow : WindowEx
{
    public Frame GetRootFrame() => RootFrame;

    public WelcomeWindow()
    {
        InitializeComponent();

        MinWidth = 980;
        MinHeight = 900;
        Title = "Wino Mail";
        this.SetIcon("Assets/Wino_Icon.ico");

        ConfigureWindowChrome();
    }

    private void ConfigureWindowChrome()
    {
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;

        Width = 980;
        Height = 720;

        //this.IsResizable = false;
        //this.IsMaximizable = false;

        this.CenterOnScreen();

        var themeService = WinoApplication.Current.Services.GetService<INewThemeService>();
        themeService?.UpdateSystemCaptionButtonColors();
    }
}
