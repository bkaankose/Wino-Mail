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
using WinRT;
using WinUIEx;

namespace Wino.Mail.WinUI;

/// <summary>
/// Fixed-size window that hosts <see cref="RelayIntroductionPage"/> for users whose install already had accounts
/// before the relay existed. Only the close caption button is shown; closing keeps timer-based sync.
/// </summary>
public sealed partial class RelayIntroductionWindow : WindowEx, IRecipient<ApplicationThemeChanged>
{
    private const double FixedWidth = 880;
    private const double FixedHeight = 560;

    public RelayIntroductionWindow()
    {
        InitializeComponent();

        Title = Translator.RelayIntroduction_WindowTitle;
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

        RootFrame.Navigate(typeof(RelayIntroductionPage), null, new SuppressNavigationTransitionInfo());
    }

    /// <summary>The theme service styles only the active window, so an open introduction follows element theme changes here.</summary>
    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
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
