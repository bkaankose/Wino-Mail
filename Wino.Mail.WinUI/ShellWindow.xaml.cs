using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Extensions;
using Wino.Mail.WinUI.Activation;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Views;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views.Mail;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class ShellWindow : WindowEx, IWinoShellWindow,
    IRecipient<ApplicationThemeChanged>,
    IRecipient<InfoBarMessageRequested>,
    IRecipient<TitleBarShellContentUpdated>,
    IRecipient<SynchronizationActionsAdded>,
    IRecipient<SynchronizationActionsCompleted>,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoAccountProfileDeletedMessage>
{
    private bool _allowClose;
    public IStatePersistanceService StatePersistanceService { get; } = WinoApplication.Current.Services.GetService<IStatePersistanceService>() ?? throw new Exception("StatePersistanceService not registered in DI container.");
    public IPreferencesService PreferencesService { get; } = WinoApplication.Current.Services.GetService<IPreferencesService>() ?? throw new Exception("PreferencesService not registered in DI container.");
    public INavigationService NavigationService { get; } = WinoApplication.Current.Services.GetService<INavigationService>() ?? throw new Exception("NavigationService not registered in DI container.");
    private IMailDialogService MailDialogService { get; } = WinoApplication.Current.Services.GetRequiredService<IMailDialogService>();
    private IWinoAccountProfileService WinoAccountProfileService { get; } = WinoApplication.Current.Services.GetRequiredService<IWinoAccountProfileService>();

    public ObservableCollection<SynchronizationActionItem> SyncActionItems { get; } = new();
    private bool _calendarReminderServerStartAttempted;
    private ITitleBarSearchHost? _activeTitleBarSearchHost;
    private bool _isBackButtonVisibilityReady;
    private bool _isSynchronizingTitleBarSearch;
    private bool _isPreparedForClose;

    public ShellWindow()
    {
        RegisterRecipients();

        InitializeComponent();
        StatePersistanceService.StatePropertyChanged += StatePersistenceServiceChanged;

        MinWidth = 420;
        MinHeight = 420;
        ConfigureTitleBar();
        ApplyTitleBarSearchHost();

        // Handle window closing event to minimize to tray instead of closing
        Closed += OnWindowClosed;

        // Use the AppWindow.Closing event to handle the close request
        AppWindow.Closing += OnAppWindowClosing;

        // Register global mouse button listener for back button
        RegisterMouseBackButtonListener();

        this.SetIcon("Assets/Wino_Icon.ico");
        Title = StatePersistanceService.AppModeTitle;
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
        WindowAppUserModelIdHelper.TrySet(this, AppEntryConstants.GetAppUserModelId(targetMode));
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

        _isBackButtonVisibilityReady = true;
        ApplyTitleBarSearchHost();
        RefreshBackButtonVisibility();
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
        ApplyTitleBarSearchHost();
        RefreshBackButtonVisibility();
    }

    public void Receive(ApplicationThemeChanged message)
    {
        UpdateTitleBarColors(message.IsUnderlyingThemeDark);
    }

    public void Receive(InfoBarMessageRequested message)
    {
        ShowInfoBarMessage(message);
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

    public void Receive(WinoAccountProfileUpdatedMessage message)
    {
        DispatcherQueue.TryEnqueue(() => UpdateWinoAccountState(message.Account));
    }

    public void Receive(WinoAccountProfileDeletedMessage message)
    {
        DispatcherQueue.TryEnqueue(() => UpdateWinoAccountState(null));
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

    private void ApplyTitleBarSearchHost()
    {
        _activeTitleBarSearchHost = ResolveActiveTitleBarSearchHost();
        SynchronizeTitleBarSearchBox();
    }

    private void StatePersistenceServiceChanged(object? sender, string propertyName)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var enqueued = DispatcherQueue.TryEnqueue(() => StatePersistenceServiceChanged(sender, propertyName));
            if (!enqueued)
                throw new InvalidOperationException("Could not marshal shell state changes onto the UI thread.");

            return;
        }

        if (propertyName == nameof(IStatePersistanceService.ApplicationMode) ||
            propertyName == nameof(IStatePersistanceService.IsReadingMail) ||
            propertyName == nameof(IStatePersistanceService.IsReaderNarrowed) ||
            propertyName == nameof(IStatePersistanceService.IsEventDetailsVisible))
        {
            RefreshBackButtonVisibility();
        }
    }

    private void RefreshBackButtonVisibility()
    {
        if (!_isBackButtonVisibilityReady)
        {
            ShellTitleBar.IsBackButtonVisible = false;
            return;
        }

        ShellTitleBar.IsBackButtonVisible = NavigationService.CanGoBack();
    }

    private ITitleBarSearchHost? ResolveActiveTitleBarSearchHost()
    {
        if (MainShellFrame.Content is WinoAppShell shellPage)
        {
            return shellPage.GetShellFrame().Content as ITitleBarSearchHost;
        }

        return MainShellFrame.Content as ITitleBarSearchHost;
    }

    private void SynchronizeTitleBarSearchBox()
    {
        _isSynchronizingTitleBarSearch = true;
        try
        {
            TitleBarSearchBox.IsEnabled = _activeTitleBarSearchHost != null;
            TitleBarSearchBox.PlaceholderText = _activeTitleBarSearchHost?.SearchPlaceholderText ?? Translator.SearchBarPlaceholder;
            TitleBarSearchBox.ItemsSource = _activeTitleBarSearchHost?.SearchSuggestions;
            TitleBarSearchBox.Text = _activeTitleBarSearchHost?.SearchText ?? string.Empty;
        }
        finally
        {
            _isSynchronizingTitleBarSearch = false;
        }
    }

    private async void TitleBarSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_isSynchronizingTitleBarSearch || _activeTitleBarSearchHost == null)
            return;

        _activeTitleBarSearchHost.SearchText = sender.Text;
        await _activeTitleBarSearchHost.OnTitleBarSearchTextChangedAsync();
    }

    private void TitleBarSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (_activeTitleBarSearchHost == null || args.SelectedItem is not TitleBarSearchSuggestion suggestion)
            return;

        _activeTitleBarSearchHost.OnTitleBarSearchSuggestionChosen(suggestion);
        SynchronizeTitleBarSearchBox();
    }

    private async void TitleBarSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_activeTitleBarSearchHost == null)
            return;

        await _activeTitleBarSearchHost.OnTitleBarSearchSubmittedAsync(args.QueryText, args.ChosenSuggestion as TitleBarSearchSuggestion);
        SynchronizeTitleBarSearchBox();
    }

    private async void OnAppWindowClosing(object sender, AppWindowClosingEventArgs e)
    {
        if (_allowClose || (Application.Current as App)?.IsExiting == true)
            return;

        e.Cancel = true;

        if (!await PrepareMailModeForHideAsync())
            return;

        var windowManager = WinoApplication.Current.Services.GetService<IWinoWindowManager>();
        windowManager?.HideWindow(this);
    }

    public void PrepareForClose()
    {
        if (_isPreparedForClose)
            return;

        _isPreparedForClose = true;

        if (MainShellFrame.Content is WinoAppShell shellPage)
        {
            shellPage.PrepareForWindowClose();
        }

        WindowCleanupHelper.CleanupFrame(MainShellFrame);

        _allowClose = true;
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        Closed -= OnWindowClosed;
        AppWindow.Closing -= OnAppWindowClosing;
        StatePersistanceService.StatePropertyChanged -= StatePersistenceServiceChanged;

        if (MainShellFrame.Content is WinoAppShell shellPage)
        {
            shellPage.PrepareForWindowClose();
        }

        WindowCleanupHelper.CleanupFrame(MainShellFrame);
        UnregisterRecipients();
    }

    private async Task<bool> PrepareMailModeForHideAsync()
    {
        if (MainShellFrame.Content is not WinoAppShell shellPage)
            return true;

        if (shellPage.GetShellFrame().Content is not MailListPage mailListPage)
            return true;

        await mailListPage.ViewModel.MailCollection.UnselectAllAsync();
        WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());

        return true;
    }

    private void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<TitleBarShellContentUpdated>(this);
        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Register<InfoBarMessageRequested>(this);
        WeakReferenceMessenger.Default.Register<SynchronizationActionsAdded>(this);
        WeakReferenceMessenger.Default.Register<SynchronizationActionsCompleted>(this);
        WeakReferenceMessenger.Default.Register<WinoAccountProfileUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<WinoAccountProfileDeletedMessage>(this);
    }

    private void UnregisterRecipients()
    {
        WeakReferenceMessenger.Default.Unregister<TitleBarShellContentUpdated>(this);
        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Unregister<InfoBarMessageRequested>(this);
        WeakReferenceMessenger.Default.Unregister<SynchronizationActionsAdded>(this);
        WeakReferenceMessenger.Default.Unregister<SynchronizationActionsCompleted>(this);
        WeakReferenceMessenger.Default.Unregister<WinoAccountProfileUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<WinoAccountProfileDeletedMessage>(this);
    }

    private void ShowInfoBarMessage(InfoBarMessageRequested message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrEmpty(message.ActionButtonTitle) || message.Action == null)
            {
                ShellInfoBar.ActionButton = null;
            }
            else
            {
                ShellInfoBar.ActionButton = new Button()
                {
                    Content = message.ActionButtonTitle,
                    Command = new RelayCommand(message.Action)
                };
            }

            ShellInfoBar.Message = message.Message;
            ShellInfoBar.Title = message.Title;
            ShellInfoBar.Severity = message.Severity.AsMUXCInfoBarSeverity();
            ShellInfoBar.IsOpen = true;
        });
    }

    private void UpdateWinoAccountState(WinoAccount? account)
    {
        var isSignedIn = account != null;

        WinoAccountSignedOutView.Visibility = isSignedIn ? Visibility.Collapsed : Visibility.Visible;
        WinoAccountSignedInView.Visibility = isSignedIn ? Visibility.Visible : Visibility.Collapsed;

        WinoAccountButtonPicture.Visibility = isSignedIn ? Visibility.Visible : Visibility.Collapsed;
        WinoAccountSignedOutIcon.Visibility = isSignedIn ? Visibility.Collapsed : Visibility.Visible;

        var initials = GetInitials(account?.Email);

        WinoAccountButtonPicture.Initials = initials;
        WinoAccountFlyoutPicture.Initials = initials;
        WinoAccountButtonPicture.DisplayName = account?.Email ?? Translator.WinoAccount_Titlebar_SignedOutTitle;
        WinoAccountFlyoutPicture.DisplayName = account?.Email ?? Translator.WinoAccount_Titlebar_SignedOutTitle;

        WinoAccountFlyoutEmailText.Text = account?.Email ?? string.Empty;
        WinoAccountFlyoutStatusText.Text = account == null
            ? string.Empty
            : string.Format(Translator.WinoAccount_Titlebar_SignedInStatus, account.AccountStatus);
    }

    private static string GetInitials(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "W";
        }

        var localPart = email.Split('@')[0];
        var segments = localPart
            .Split(['.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Take(2)
            .ToArray();

        if (segments.Length == 0)
        {
            return email[..1].ToUpperInvariant();
        }

        return string.Concat(segments.Select(segment => char.ToUpperInvariant(segment[0])));
    }

    private async void RegisterWinoAccountClicked(object sender, RoutedEventArgs e)
    {
        WinoAccountFlyout.Hide();
        var account = await MailDialogService.ShowWinoAccountRegistrationDialogAsync();
        if (account != null)
        {
            ShowInfoBarMessage(new InfoBarMessageRequested(
                InfoBarMessageType.Success,
                Translator.GeneralTitle_Info,
                string.Format(Translator.WinoAccount_RegisterSuccessMessage, account.Email)));
        }
    }

    private async void LoginWinoAccountClicked(object sender, RoutedEventArgs e)
    {
        WinoAccountFlyout.Hide();
        var account = await MailDialogService.ShowWinoAccountLoginDialogAsync();
        if (account != null)
        {
            ShowInfoBarMessage(new InfoBarMessageRequested(
                InfoBarMessageType.Success,
                Translator.GeneralTitle_Info,
                string.Format(Translator.WinoAccount_LoginSuccessMessage, account.Email)));
        }
    }

    private async void SignOutWinoAccountClicked(object sender, RoutedEventArgs e)
    {
        var activeAccount = await WinoAccountProfileService.GetActiveAccountAsync();
        if (activeAccount == null)
        {
            ShowInfoBarMessage(new InfoBarMessageRequested(
                InfoBarMessageType.Warning,
                Translator.GeneralTitle_Info,
                Translator.WinoAccount_SignOut_NoAccountMessage));
            return;
        }

        await WinoAccountProfileService.SignOutAsync();

        ShowInfoBarMessage(new InfoBarMessageRequested(
            InfoBarMessageType.Success,
            Translator.GeneralTitle_Info,
            string.Format(Translator.WinoAccount_SignOut_SuccessMessage, activeAccount.Email)));
    }

}
