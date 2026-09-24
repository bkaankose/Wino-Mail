using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Helpers;
using Wino.Messaging.Client.Shell;
using Wino.Views.Misc;
using WinUIEx;

namespace Wino.Mail.WinUI;

/// <summary>
/// Fixed-size window that hosts <see cref="WhatsNewPage"/>. Only the close caption button is shown.
/// </summary>
public sealed partial class WhatsNewWindow : WindowEx, IRecipient<ApplicationThemeChanged>
{
    // The illustration slot of WhatsNewPage is 560x300; the window is sized around it.
    private const double FixedWidth = 880;
    private const double FixedHeight = 640;

    public WhatsNewWindow()
    {
        InitializeComponent();

        Title = Translator.WhatsNew_WindowTitle;
        this.SetIcon("Assets/Wino_Icon.ico");

        IsResizable = false;
        IsMaximizable = false;
        IsMinimizable = false;
        MinWidth = FixedWidth;
        MinHeight = FixedHeight;
        Width = FixedWidth;
        Height = FixedHeight;
        this.CenterOnScreen();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        var underlyingThemeService = WinoApplication.Current.Services.GetService<IUnderlyingThemeService>();
        if (underlyingThemeService != null)
        {
            SystemCaptionButtonColorHelper.Apply(AppWindow.TitleBar, underlyingThemeService.IsUnderlyingThemeDark());
        }

        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
        Closed += OnWindowClosed;

        RootFrame.Navigate(typeof(WhatsNewPage), null, new SuppressNavigationTransitionInfo());
    }

    /// <summary>
    /// The theme service styles only the active window, so an open What's New window follows
    /// element theme changes here. Backdrop and custom theme changes apply the next time it opens.
    /// </summary>
    public void Receive(ApplicationThemeChanged message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var themeService = WinoApplication.Current.Services.GetService<INewThemeService>();
            if (themeService != null && Content is FrameworkElement root)
            {
                root.RequestedTheme = themeService.RootTheme.ToWindowsElementTheme();
            }

            SystemCaptionButtonColorHelper.Apply(AppWindow.TitleBar, message.IsUnderlyingThemeDark);
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        Closed -= OnWindowClosed;
        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
        WindowCleanupHelper.CleanupFrame(RootFrame);
    }
}
