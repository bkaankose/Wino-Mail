using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.WinUI.Activation;
using Wino.Mail.WinUI.Interfaces;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class ShellWindow : WindowEx, IWinoShellWindow,
    IRecipient<ApplicationThemeChanged>,
    IRecipient<TitleBarShellContentUpdated>,
    IRecipient<SynchronizationActionsAdded>,
    IRecipient<SynchronizationActionsCompleted>
{
    public IStatePersistanceService StatePersistanceService { get; } = WinoApplication.Current.Services.GetService<IStatePersistanceService>() ?? throw new Exception("StatePersistanceService not registered in DI container.");
    public IPreferencesService PreferencesService { get; } = WinoApplication.Current.Services.GetService<IPreferencesService>() ?? throw new Exception("PreferencesService not registered in DI container.");
    public INavigationService NavigationService { get; } = WinoApplication.Current.Services.GetService<INavigationService>() ?? throw new Exception("NavigationService not registered in DI container.");

    public ICommand ShowWinoCommand { get; set; }
    public ICommand ExitWinoCommand { get; set; }

    public ObservableCollection<SynchronizationActionItem> SyncActionItems { get; } = new();
    private bool _calendarReminderServerStartAttempted;
    private bool _isApplyingActivationMode;
    private WinoApplicationMode _currentMode = WinoApplicationMode.Mail;

    public ShellWindow()
    {
        RegisterRecipients();

        InitializeComponent();

        MinWidth = 420;
        MinHeight = 420;
        ConfigureTitleBar();

        // Handle window closing event to minimize to tray instead of closing
        Closed += OnWindowClosed;

        // Use the AppWindow.Closing event to handle the close request
        AppWindow.Closing += OnAppWindowClosing;

        // Register global mouse button listener for back button
        RegisterMouseBackButtonListener();

        ShowWinoCommand = new RelayCommand(RestoreFromTray);
        ExitWinoCommand = new RelayCommand(ForceClose);

        this.SetIcon("Assets/Wino_Icon.ico");
        Title = "Wino Mail";

        SystemTrayIcon.ForceCreate();
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

    private void RegisterMouseBackButtonListener()
    {
        // Subscribe to pointer pressed events on the root content
        if (Content is UIElement rootElement)
        {
            rootElement.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPointerPressed), true);
        }
    }

    private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Check if it's the back button (XButton1)
        var pointerPoint = e.GetCurrentPoint(null);
        var properties = pointerPoint.Properties;

        // XButton1 is the back button on most mice
        if (properties.IsXButton1Pressed)
        {
            // Call GoBack on NavigationService
            NavigationService.GoBack();
            e.Handled = true;
        }
    }

    public void HandleAppActivation(string? launchArguments, string? tileId = null, string? appId = null)
    {
        var targetMode = AppModeActivationResolver.Resolve(launchArguments, tileId, appId, PreferencesService.DefaultApplicationMode);
        _currentMode = targetMode;

        _isApplyingActivationMode = true;
        AppModeSegmentedControl.SelectedIndex = targetMode == WinoApplicationMode.Mail ? 0 : 1;
        _isApplyingActivationMode = false;

        NavigationService.ChangeApplicationMode(targetMode);
    }

    public Microsoft.UI.Xaml.Controls.TitleBar GetTitleBar() => ShellTitleBar;

    public Frame GetMainFrame() => MainShellFrame;

    public FrameworkElement GetRootContent() => Content as Grid ?? throw new Exception("RootContent is not a Grid or empty.");

    private void BackButtonClicked(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
    {
        NavigationService.GoBack();
    }

    private void MainFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (!_calendarReminderServerStartAttempted)
        {
            _calendarReminderServerStartAttempted = true;
            _ = StartCalendarReminderServerAsync();
        }

        // Mail shell has shell content only for mail list page
        // Thus, we check if the current content is MailAppShell

        if (sender is Frame mainFrame && mainFrame.Content is MailAppShell mailAppShellPage)
            ShellTitleBar.Content = mailAppShellPage.TopShellContent;
        else if (e.Content is BasePage basePage)
            ShellTitleBar.Content = basePage.ShellContent;
    }

    private async Task StartCalendarReminderServerAsync()
    {
        try
        {
            var reminderServer = WinoApplication.Current.Services.GetService<ICalendarReminderServer>();
            if (reminderServer != null)
            {
                await reminderServer.StartAsync();
            }
        }
        catch (Exception ex)
        {
            _calendarReminderServerStartAttempted = false;
            Serilog.Log.Error(ex, "Failed to start calendar reminder server.");
        }
    }

    private void PaneButtonClicked(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
    {
        PreferencesService.IsNavigationPaneOpened = !PreferencesService.IsNavigationPaneOpened;
    }

    public void Receive(TitleBarShellContentUpdated message)
    {
        if (MainShellFrame.Content is MailAppShell shellPage)
        {
            ShellTitleBar.Content = shellPage.TopShellContent;
        }
    }

    public void Receive(ApplicationThemeChanged message)
    {
        UpdateTitleBarColors(message.IsUnderlyingThemeDark);
    }

    public void Receive(SynchronizationActionsAdded message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var action in message.Actions)
                SyncActionItems.Add(action);

            UpdateSyncStatusVisibility();
        });
    }

    public void Receive(SynchronizationActionsCompleted message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var toRemove = SyncActionItems.Where(a => a.AccountId == message.AccountId).ToList();

            foreach (var item in toRemove)
                SyncActionItems.Remove(item);

            UpdateSyncStatusVisibility();
        });
    }

    private void UpdateSyncStatusVisibility()
    {
        SyncStatusButton.Visibility = SyncActionItems.Any()
            ? Visibility.Visible
            : Visibility.Collapsed;

        var distinctAccounts = SyncActionItems.Select(a => a.AccountId).Distinct().Count();

        SyncStatusText.Text = distinctAccounts switch
        {
            0 => string.Empty,
            1 => string.Format(Translator.SyncAction_SynchronizingAccount, SyncActionItems.First().AccountName),
            _ => string.Format(Translator.SyncAction_SynchronizingAccounts, distinctAccounts)
        };
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
        e.Cancel = true;
        MinimizeToTray();
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        SystemTrayIcon?.Dispose();
    }

    private void MinimizeToTray()
    {
        this.Hide();
        SystemTrayIcon.ForceCreate();
    }

    private void RestoreFromTray()
    {

        this.Show();
        BringToFront();
    }

    public void ForceClose()
    {
        // Unsubscribe from the closing event to avoid infinite loop
        AppWindow.Closing -= OnAppWindowClosing;

        // Clean up system tray
        SystemTrayIcon?.Dispose();

        UnregisterRecipients();

        var windowManager = WinoApplication.Current.Services.GetService<IWinoWindowManager>();
        windowManager?.CloseAllWindows();

        // Exit the application
        Application.Current.Exit();
    }

    private void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<TitleBarShellContentUpdated>(this);
        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Register<SynchronizationActionsAdded>(this);
        WeakReferenceMessenger.Default.Register<SynchronizationActionsCompleted>(this);
    }

    private void UnregisterRecipients()
    {
        WeakReferenceMessenger.Default.Unregister<TitleBarShellContentUpdated>(this);
        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Unregister<SynchronizationActionsAdded>(this);
        WeakReferenceMessenger.Default.Unregister<SynchronizationActionsCompleted>(this);
    }

    private void SegmentedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingActivationMode || sender is not Segmented segmentedControl)
            return;

        var selectedMode = segmentedControl.SelectedIndex == 1
            ? WinoApplicationMode.Calendar
            : WinoApplicationMode.Mail;

        if (selectedMode == _currentMode)
            return;

        _currentMode = selectedMode;
        NavigationService.ChangeApplicationMode(selectedMode);
    }
}
