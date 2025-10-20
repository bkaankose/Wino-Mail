using System;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Wino.Core.Domain.Interfaces;
using Wino.Core.WinUI;
using Wino.Core.WinUI.Interfaces;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class ShellWindow : WindowEx, IWinoShellWindow, IRecipient<ApplicationThemeChanged>, IRecipient<TitleBarShellContentUpdated>
{
    public IStatePersistanceService StatePersistanceService { get; } = WinoApplication.Current.Services.GetService<IStatePersistanceService>() ?? throw new Exception("StatePersistanceService not registered in DI container.");
    public IPreferencesService PreferencesService { get; } = WinoApplication.Current.Services.GetService<IPreferencesService>() ?? throw new Exception("PreferencesService not registered in DI container.");
    private readonly ISystemTrayService _systemTrayService;

    public ShellWindow()
    {
        RegisterRecipients();

        InitializeComponent();

        MinWidth = 420;
        MinHeight = 420;
        ConfigureTitleBar();

        // Initialize system tray service
        _systemTrayService = WinoApplication.Current.Services.GetService<ISystemTrayService>() ?? throw new Exception("SystemTrayService not registered in DI container.");
        _systemTrayService.Initialize();
        _systemTrayService.TrayIconDoubleClicked += OnTrayIconDoubleClicked;

        // Handle window closing event to minimize to tray instead of closing
        Closed += OnWindowClosed;

        // Use the AppWindow.Closing event to handle the close request
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void ConfigureTitleBar()
    {
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        
        // Apply initial theme colors
        var themeService = WinoApplication.Current.Services.GetService<INewThemeService>();
        if (themeService != null)
        {
            var underlyingThemeService = WinoApplication.Current.Services.GetService<IUnderlyingThemeService>();
            if (underlyingThemeService != null)
            {
                UpdateTitleBarColors(underlyingThemeService.IsUnderlyingThemeDark());
            }
        }
    }

    public void HandleAppActivation(LaunchActivatedEventArgs args)
    {
        // TODO: Handle protocol activations.

        MainShellFrame.Navigate(typeof(AppShell));
    }

    public Microsoft.UI.Xaml.Controls.TitleBar GetTitleBar() => ShellTitleBar;

    public Frame GetMainFrame() => MainShellFrame;

    public FrameworkElement GetRootContent() => Content as Grid ?? throw new Exception("RootContent is not a Grid or empty.");

    private void BackButtonClicked(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
    {
        WeakReferenceMessenger.Default.Send(new ClearMailSelectionsRequested());
        WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
    }

    private void MainFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) => ShellTitleBar.Content = (e.Content as BasePage).ShellContent;

    private void PaneButtonClicked(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
    {
        PreferencesService.IsNavigationPaneOpened = !PreferencesService.IsNavigationPaneOpened;
    }

    public void Receive(TitleBarShellContentUpdated message)
    {
        if (MainShellFrame.Content is AppShell shellPage)
        {
            ShellTitleBar.Content = shellPage.TopShellContent;
        }
    }

    public void Receive(ApplicationThemeChanged message)
    {
        UpdateTitleBarColors(message.IsUnderlyingThemeDark);
    }

    private void UpdateTitleBarColors(bool isDarkTheme)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var titleBar = AppWindow.TitleBar;
            if (titleBar == null) return;

            // Set button colors based on theme
            // Background is always transparent for all buttons
            titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0); // Transparent
            titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0); // Transparent
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0, 0, 0, 0); // Transparent
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0, 0, 0, 0); // Transparent

            if (isDarkTheme)
            {
                // Dark theme: use light text/icons for better contrast
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 255, 255, 255); // White
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(128, 255, 255, 255); // Semi-transparent white
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255); // White
                titleBar.ButtonPressedForegroundColor = Color.FromArgb(200, 255, 255, 255); // Slightly dimmed white
            }
            else
            {
                // Light theme: use dark text/icons for better contrast
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 0, 0, 0); // Black
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(128, 0, 0, 0); // Semi-transparent black
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 0, 0, 0); // Black
                titleBar.ButtonPressedForegroundColor = Color.FromArgb(200, 0, 0, 0); // Slightly dimmed black
            }
        });
    }

    private void OnAppWindowClosing(object sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        // Cancel the close and minimize to tray instead
        e.Cancel = true;
        MinimizeToTray();
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        // Clean up tray icon when window is actually closed
        _systemTrayService?.Dispose();
    }

    private void MinimizeToTray()
    {
        // Hide the window and show tray icon
        this.Hide();
        _systemTrayService.Show();
    }

    private void OnTrayIconDoubleClicked(object? sender, EventArgs e)
    {
        // Restore the window from tray
        RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        if (_systemTrayService.IsMinimizedToTray)
        {
            // Show the window and hide tray icon
            this.Show();
            this.Activate();
            _systemTrayService.Hide();
        }
    }

    public void ForceClose()
    {
        // Unsubscribe from the closing event to avoid infinite loop
        AppWindow.Closing -= OnAppWindowClosing;
        
        // Clean up system tray
        _systemTrayService?.Dispose();
        
        UnregisterRecipients();
        
        // Close the window
        this.Close();
        
        // Exit the application
        Application.Current.Exit();
    }

    private void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<TitleBarShellContentUpdated>(this);
        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
    }

    private void UnregisterRecipients()
    {
        WeakReferenceMessenger.Default.Unregister<TitleBarShellContentUpdated>(this);
        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
    }
}
