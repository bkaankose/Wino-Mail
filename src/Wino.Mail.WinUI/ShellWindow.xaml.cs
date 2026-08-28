using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.Navigation;
using Wino.Extensions;
using Wino.Mail.WinUI.Activation;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Views;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views.Mail;
using WinUIEx;

namespace Wino.Mail.WinUI;

public sealed partial class ShellWindow : WindowEx, IWinoShellWindow,
    IWinoFrameProvider,
    IRecipient<ApplicationThemeChanged>,
    IRecipient<InfoBarMessageRequested>,
    IRecipient<TitleBarShellContentUpdated>,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoAccountProfileDeletedMessage>,
    IRecipient<DailyBriefingStateChanged>,
    IRecipient<WinoIntelligenceAccessChanged>,
    IRecipient<AccountSynchronizationProgressUpdatedMessage>
{
    private const int AutomaticPlacementRestorationBehaviorValue = 1;
    private static readonly Guid ShellWindowPersistedStateId = new("6BEB6E1D-BEAF-4CE7-9967-13B2A4F46187");

    private bool _allowClose;
    public IStatePersistanceService StatePersistanceService { get; } = WinoApplication.Current.Services.GetService<IStatePersistanceService>() ?? throw new Exception("StatePersistanceService not registered in DI container.");
    public IPreferencesService PreferencesService { get; } = WinoApplication.Current.Services.GetService<IPreferencesService>() ?? throw new Exception("PreferencesService not registered in DI container.");
    public INavigationService NavigationService { get; } = WinoApplication.Current.Services.GetService<INavigationService>() ?? throw new Exception("NavigationService not registered in DI container.");
    private IMailDialogService MailDialogService { get; } = WinoApplication.Current.Services.GetRequiredService<IMailDialogService>();
    private IWinoAccountProfileService WinoAccountProfileService { get; } = WinoApplication.Current.Services.GetRequiredService<IWinoAccountProfileService>();
    private IWinoBillingService WinoBillingService { get; } = WinoApplication.Current.Services.GetRequiredService<IWinoBillingService>();
    private ILocalIntelligenceService LocalIntelligenceService { get; } = WinoApplication.Current.Services.GetRequiredService<ILocalIntelligenceService>();

    private bool _calendarReminderServerStartAttempted;
    private ITitleBarSearchHost? _activeTitleBarSearchHost;
    private IShellMenuProvider? _activeSynchronizationProvider;
    private float? _shellTitleOpacity;
    private bool _isBackButtonVisibilityReady;
    private bool _isSynchronizingTitleBarSearch;
    private bool _hasDailyBriefingAccess;
    private ISearchHistoryService SearchHistoryService { get; } = WinoApplication.Current.Services.GetRequiredService<ISearchHistoryService>();
    private bool _isPreparedForClose;
    private bool _isCloseRequestInProgress;
    private readonly Microsoft.UI.Xaml.Input.PointerEventHandler _pointerPressedHandler;

    public ShellWindow()
    {
        InitializeComponent();
        _pointerPressedHandler = OnPointerPressed;
        RegisterRecipients();
        StatePersistanceService.StatePropertyChanged += StatePersistenceServiceChanged;
        PreferencesService.PreferenceChanged += PreferencesServiceChanged;
        DailyBriefingPanelControl.IsOpenChanged += DailyBriefingPanelIsOpenChanged;

        MinWidth = 420;
        MinHeight = 420;
        ConfigureWindowPlacementPersistence();
        ConfigureTitleBar();
        UpdateShellTitles();
        UpdateWinoAccountButtonVisibility();
        ApplyTitleBarSearchHost();
        ApplyShellSynchronizationProvider();
        _ = RefreshDailyBriefingStateAsync();

        // Handle window closing event for terminate vs background/tray behavior.
        Closed += OnWindowClosed;

        // Use the AppWindow.Closing event to handle the close request
        AppWindow.Closing += OnAppWindowClosing;

        // Register global mouse button listener for back button
        RegisterMouseBackButtonListener();

        this.SetIcon("Assets/Wino_Icon.ico");

        AttachTitleBarWidthStates();
    }

    /// <summary>
    /// The adaptive triggers live in XAML; applying their result does not. A VisualState Setter can
    /// only reach a named element in its own namescope, and everything the width states have to
    /// touch sits inside the TitleBar's template, so the state change is forwarded here instead.
    /// </summary>
    private void AttachTitleBarWidthStates()
    {
        var group = VisualStateManager.GetVisualStateGroups(TitleBarStateHost).FirstOrDefault(x => x.Name == "TitleBarWidthStates");
        if (group is null) return;

        // The triggers move CurrentState on their own, but the change notification is not something
        // to lean on here, so the state is also re-read after every resize and after load.
        group.CurrentStateChanged += (_, e) => ApplyTitleBarWidthState(e.NewState?.Name);
        TitleBarStateHost.Loaded += (_, _) => ApplyTitleBarWidthState(group.CurrentState?.Name);
        ShellRoot.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(() => ApplyTitleBarWidthState(group.CurrentState?.Name));
    }

    private void ApplyTitleBarWidthState(string? stateName)
    {
        var isCompact = stateName is not null and not "WideTitleBarState";

        ShellTitleHost.Visibility = stateName == "MinimalTitleBarState" ? Visibility.Collapsed : Visibility.Visible;
        TitleBarSearchBox.IsCompact = isCompact;

        // The icon is the whole control in compact mode, so the wide layout's width floor has to go
        // with it; leaving MinWidth at 400 would keep an empty 400px hole in the middle of the bar.
        TitleBarSearchBox.MinWidth = isCompact ? 0 : 400;
        TitleBarSearchBox.MaxWidth = isCompact ? 48 : 520;
        TitleBarSearchBox.HorizontalAlignment = isCompact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
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

    private void ConfigureWindowPlacementPersistence()
    {
        AppWindow.PersistedStateId = ShellWindowPersistedStateId;
#pragma warning disable CS8305 // PlacementRestorationBehavior is experimental in Windows App SDK 2.0.
        AppWindow.PlacementRestorationBehavior = (PlacementRestorationBehavior)AutomaticPlacementRestorationBehaviorValue;
#pragma warning restore CS8305
    }

    private void RegisterMouseBackButtonListener()
    {
        // Subscribe to pointer pressed events on the root content
        if (Content is UIElement rootElement)
        {
            rootElement.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, true);
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
        var targetMode = AppModeActivationResolver.Resolve(launchArguments, tileId, appId, WinoApplicationMode.Mail);
        WindowAppUserModelIdHelper.TrySet(this, AppEntryConstants.GetAppUserModelId(targetMode));

        if (TryCreateMailFolderLaunchRequest(launchArguments, out var folderLaunchRequest))
        {
            NavigationService.RestoreShell(targetMode, new ShellModeActivationContext
            {
                Parameter = folderLaunchRequest
            });

            return;
        }

        NavigationService.RestoreShell(targetMode);
    }

    private static bool TryCreateMailFolderLaunchRequest(string? launchArguments, out MailFolderLaunchRequest? request)
    {
        request = null;

        var arguments = NotificationArguments.Parse(launchArguments);

        if (!arguments.TryGetValue(Constants.JumpListActionKey, out var action) ||
            !string.Equals(action, Constants.JumpListOpenMailFolderAction, StringComparison.Ordinal) ||
            !arguments.TryGetValue(Constants.JumpListAccountIdKey, out var accountIdString) ||
            !arguments.TryGetValue(Constants.JumpListFolderIdKey, out var folderIdString) ||
            !Guid.TryParse(accountIdString, out var accountId) ||
            !Guid.TryParse(folderIdString, out var folderId))
        {
            return false;
        }

        request = new MailFolderLaunchRequest(accountId, folderId);
        return true;
    }

    public Microsoft.UI.Xaml.Controls.TitleBar GetTitleBar() => ShellTitleBar;

    public Frame GetMainFrame() => MainShellFrame;

    public Frame? GetFrame(NavigationReferenceFrame frameType)
        => frameType == NavigationReferenceFrame.ShellFrame ? MainShellFrame : null;

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
        ApplyShellSynchronizationProvider();
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
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTitleBarSearchHost();
            ApplyShellSynchronizationProvider();
            RefreshBackButtonVisibility();
        });
    }

    public void Receive(ApplicationThemeChanged message)
    {
        DispatcherQueue.TryEnqueue(() => UpdateTitleBarColors(message.IsUnderlyingThemeDark));
    }

    public void Receive(InfoBarMessageRequested message)
    {
        DispatcherQueue.TryEnqueue(() => ShowInfoBarMessage(message));
    }

    public void Receive(WinoAccountProfileUpdatedMessage message)
    {
        DispatcherQueue.TryEnqueue(() => UpdateWinoAccountState(message.Account));
        _ = RefreshDailyBriefingStateAsync();
    }

    public void Receive(WinoAccountProfileDeletedMessage message)
    {
        DispatcherQueue.TryEnqueue(() => UpdateWinoAccountState(null));
        _ = RefreshDailyBriefingStateAsync();
    }

    public async void Receive(DailyBriefingStateChanged message) => await RefreshDailyBriefingStateAsync();

    public async void Receive(WinoIntelligenceAccessChanged message) => await RefreshDailyBriefingStateAsync();

    private async void DailyBriefingToggleButtonClicked(object sender, RoutedEventArgs e)
    {
        await DailyBriefingPanelControl.ToggleAsync();
        DailyBriefingToggleButton.IsChecked = DailyBriefingPanelControl.IsOpen;
    }

    private void DailyBriefingPanelIsOpenChanged(object? sender, bool isOpen)
        => DailyBriefingToggleButton.IsChecked = isOpen;

    private async Task RefreshDailyBriefingStateAsync()
    {
        try
        {
            var winoAccount = await WinoAccountProfileService.GetAuthenticatedAccountAsync().ConfigureAwait(false);
            var billing = winoAccount == null
                ? null
                : await WinoBillingService.GetStatusAsync().ConfigureAwait(false);
            var hasAccess = billing?.IsSuccess == true && billing.Result?.AiPack?.HasAccess == true;
            var eligible = hasAccess
                ? await LocalIntelligenceService.GetEligibleAccountsAsync().ConfigureAwait(false)
                : [];
            var unseen = eligible.Count > 0
                ? await LocalIntelligenceService.GetUnseenStateAsync().ConfigureAwait(false)
                : new DailyBriefingUnseenState(false, null);
            DispatcherQueue.TryEnqueue(() =>
            {
                _hasDailyBriefingAccess = hasAccess;
                DailyBriefingUnseenBadge.Visibility = unseen.HasUnseenContent ? Visibility.Visible : Visibility.Collapsed;
                RefreshDailyBriefingButtonVisibility();
            });
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Failed to refresh the Daily Briefing title-bar state.");
            DispatcherQueue.TryEnqueue(() =>
            {
                _hasDailyBriefingAccess = false;
                DailyBriefingUnseenBadge.Visibility = Visibility.Collapsed;
                RefreshDailyBriefingButtonVisibility();
            });
        }
    }

    private void RefreshDailyBriefingButtonVisibility()
    {
        var isMailMode = StatePersistanceService.ApplicationMode == WinoApplicationMode.Mail;
        DailyBriefingToggleButton.Visibility = isMailMode && _hasDailyBriefingAccess
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        if (_activeTitleBarSearchHost is IMailTitleBarSearchHost previousMailHost)
            previousMailHost.SemanticSearchBusyChanged -= MailHostSemanticSearchBusyChanged;
        _activeTitleBarSearchHost = ResolveActiveTitleBarSearchHost();
        if (_activeTitleBarSearchHost is IMailTitleBarSearchHost mailHost)
            mailHost.SemanticSearchBusyChanged += MailHostSemanticSearchBusyChanged;
        SynchronizeTitleBarSearchBox(resetMeaning: true);
        _ = RefreshSemanticAvailabilityAsync();
    }

    private void MailHostSemanticSearchBusyChanged(object? sender, bool isBusy)
    {
        if (!ReferenceEquals(sender, _activeTitleBarSearchHost))
            return;
        DispatcherQueue.TryEnqueue(() => TitleBarSearchBox.IsSemanticSearchBusy = isBusy);
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

        if (propertyName is nameof(IStatePersistanceService.AppModeTitle)
            or nameof(IStatePersistanceService.CoreWindowTitle))
        {
            UpdateShellTitles();
        }

        // The briefing panel belongs to the mail surface it was opened over. A mode switch replaces
        // that surface, so the panel goes with it instead of hanging over the new one.
        if (propertyName == nameof(IStatePersistanceService.ApplicationMode))
        {
            DailyBriefingPanelControl.Close();
            RefreshDailyBriefingButtonVisibility();

            var applicationMode = StatePersistanceService.ApplicationMode;

            // Settings belongs to whichever app entry opened it. The three primary modes,
            // however, must follow their own taskbar entries even when switched in-app.
            if (applicationMode != WinoApplicationMode.Settings)
            {
                WindowAppUserModelIdHelper.TrySet(this, AppEntryConstants.GetAppUserModelId(applicationMode));
            }
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
            return shellPage.GetFrame(NavigationReferenceFrame.InnerShellFrame)?.Content as ITitleBarSearchHost;
        }

        return MainShellFrame.Content as ITitleBarSearchHost;
    }

    private void SynchronizeTitleBarSearchBox(bool resetMeaning = false)
    {
        _isSynchronizingTitleBarSearch = true;
        try
        {
            TitleBarSearchBox.IsEnabled = _activeTitleBarSearchHost != null;
            TitleBarSearchBox.Mode = _activeTitleBarSearchHost?.SearchMode ?? SearchBarMode.Mail;
            TitleBarSearchBox.PlaceholderText = _activeTitleBarSearchHost?.SearchPlaceholderText ?? Translator.SearchBarPlaceholder;
            TitleBarSearchBox.ItemsSource = _activeTitleBarSearchHost?.SearchSuggestions;
            TitleBarSearchBox.Text = _activeTitleBarSearchHost?.SearchText ?? string.Empty;
            TitleBarSearchBox.SearchHistoryItemsSource = _activeTitleBarSearchHost is null
                ? null
                : SearchHistoryService.GetHistory(_activeTitleBarSearchHost.SearchMode);
            TitleBarSearchBox.ReachOptionsSource = new SearchBarOptionItem[]
            {
                new((int)SearchBarReach.DownloadedOnly, Translator.SearchBar_ReachDownloaded),
                new((int)SearchBarReach.IncludeServer, Translator.SearchBar_ReachServer),
            };
            TitleBarSearchBox.DateOptionsSource = new SearchBarOptionItem[]
            {
                new((int)SearchBarDateRange.AnyTime, Translator.SearchBar_DateAnyTime),
                new((int)SearchBarDateRange.Today, Translator.SearchBar_DateToday),
                new((int)SearchBarDateRange.LastSevenDays, Translator.SearchBar_DateLastSevenDays),
                new((int)SearchBarDateRange.LastThirtyDays, Translator.SearchBar_DateLastThirtyDays),
            };

            if (_activeTitleBarSearchHost is IMailTitleBarSearchHost mailHost)
            {
                TitleBarSearchBox.ScopeOptionsSource = mailHost.ScopeOptions;
                TitleBarSearchBox.IsSemanticSearchAvailable = mailHost.IsSemanticSearchAvailable;
                TitleBarSearchBox.IsSemanticSearchBusy = mailHost.IsSemanticSearchBusy;
                TitleBarSearchBox.SemanticUnavailableReasonText = mailHost.SemanticUnavailableReasonText;
                TitleBarSearchBox.SenderSuggestions = mailHost.SenderSuggestions;
                if (resetMeaning)
                    TitleBarSearchBox.IsSemanticSearchEnabled = false;
            }
            else
            {
                TitleBarSearchBox.IsSemanticSearchAvailable = false;
                TitleBarSearchBox.IsSemanticSearchBusy = false;
                TitleBarSearchBox.IsSemanticSearchEnabled = false;
            }
        }
        finally
        {
            _isSynchronizingTitleBarSearch = false;
        }
    }

    private async void TitleBarSearchTextChanged(object? sender, SearchBarTextChangedEventArgs args)
    {
        if (_isSynchronizingTitleBarSearch || _activeTitleBarSearchHost == null)
            return;

        _activeTitleBarSearchHost.SearchText = args.Text;
        await _activeTitleBarSearchHost.OnTitleBarSearchTextChangedAsync();
    }

    private void TitleBarClearSearchHistoryRequested(object? sender, EventArgs e)
    {
        if (_activeTitleBarSearchHost == null)
            return;

        SearchHistoryService.Clear(_activeTitleBarSearchHost.SearchMode);
        TitleBarSearchBox.SearchHistoryItemsSource = [];
    }

    private async Task RefreshSemanticAvailabilityAsync(SearchBarFilterSnapshot? filters = null)
    {
        if (_activeTitleBarSearchHost is not IMailTitleBarSearchHost mailHost)
            return;
        filters ??= new(
            TitleBarSearchBox.SearchScope,
            TitleBarSearchBox.SearchReach,
            TitleBarSearchBox.SenderFilter,
            TitleBarSearchBox.DateRange,
            TitleBarSearchBox.HasAttachments,
            TitleBarSearchBox.IsUnread,
            TitleBarSearchBox.IsFlagged);
        var availability = await mailHost.GetSemanticSearchAvailabilityAsync(filters).ConfigureAwait(false);
        await DispatcherQueue.EnqueueAsync(() =>
        {
            if (!ReferenceEquals(mailHost, _activeTitleBarSearchHost)) return;
            TitleBarSearchBox.IsSemanticSearchAvailable = availability.IsAvailable;
            TitleBarSearchBox.SemanticUnavailableReasonText = availability.UnavailableReason;
            if (!availability.IsAvailable) TitleBarSearchBox.IsSemanticSearchEnabled = false;
        });
    }

    private async void TitleBarSenderSuggestionsRequested(object? sender, SearchBarSenderQueryEventArgs args)
    {
        if (_activeTitleBarSearchHost is not IMailTitleBarSearchHost mailHost)
            return;

        await mailHost.RequestSenderSuggestionsAsync(args.QueryText);
        if (ReferenceEquals(mailHost, _activeTitleBarSearchHost))
            TitleBarSearchBox.SenderSuggestions = mailHost.SenderSuggestions;
    }

    private async void TitleBarSearchSubmitted(object? sender, SearchBarSubmittedEventArgs args)
    {
        if (_activeTitleBarSearchHost == null)
            return;

        SearchHistoryService.Record(args.Mode, args.QueryText);
        if (_activeTitleBarSearchHost is IMailTitleBarSearchHost mailHost)
            await mailHost.OnMailSearchSubmittedAsync(args);
        else
        {
            var suggestion = args.ChosenSuggestion as TitleBarSearchSuggestion;
            if (suggestion is not null)
                _activeTitleBarSearchHost.OnTitleBarSearchSuggestionChosen(suggestion);
            await _activeTitleBarSearchHost.OnTitleBarSearchSubmittedAsync(args.QueryText, suggestion);
        }

        SynchronizeTitleBarSearchBox();
    }

    private async void OnAppWindowClosing(object sender, AppWindowClosingEventArgs e)
    {
        var app = Application.Current as App;

        if (_allowClose || app?.IsExiting == true)
            return;

        // Snapshot the preference once so a single close request cannot take different branches
        // before and after asynchronous draft/compose confirmation.
        var closeBehavior = PreferencesService.AppCloseBehavior;

        if (app?.TryExitApplicationOnShellWindowClose(closeBehavior) == true)
            return;

        e.Cancel = true;

        if (_isCloseRequestInProgress)
            return;

        _isCloseRequestInProgress = true;

        try
        {
            if (!await PrepareMailModeForCloseAsync())
                return;

            if (app?.TryPrepareForBackgroundShellWindowClose(closeBehavior) != true)
                return;

            SaveWindowPlacement();
            PrepareForClose();

            // PrepareForClose removes this handler and permits the real close. The managed
            // app and tray keep running, but this HWND and its complete XAML tree do not.
            Close();
        }
        finally
        {
            _isCloseRequestInProgress = false;
        }
    }

    private void PreferencesServiceChanged(object? sender, string propertyName)
    {
        if (propertyName != nameof(IPreferencesService.IsWinoAccountButtonHidden))
            return;

        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateWinoAccountButtonVisibility();
            return;
        }

        DispatcherQueue.TryEnqueue(UpdateWinoAccountButtonVisibility);
    }

    private void UpdateShellTitles()
    {
        // The TitleBar's own Title/Subtitle stay unset. Rendering them ourselves is what
        // lets a running synchronization fade them without collapsing their columns.
        ShellTitleText.Text = StatePersistanceService.AppModeTitle;
        ShellSubtitleText.Text = StatePersistanceService.CoreWindowTitle;
        Title = string.IsNullOrWhiteSpace(StatePersistanceService.CoreWindowTitle)
            ? StatePersistanceService.AppModeTitle
            : $"{StatePersistanceService.AppModeTitle} - {StatePersistanceService.CoreWindowTitle}";
    }

    private void UpdateWinoAccountButtonVisibility()
        => WinoAccountButton.Visibility = PreferencesService.IsWinoAccountButtonHidden
            ? Visibility.Collapsed
            : Visibility.Visible;

    public void PrepareForClose()
    {
        if (_isPreparedForClose)
            return;

        _isPreparedForClose = true;
        _allowClose = true;

        AppWindow.Closing -= OnAppWindowClosing;
        StatePersistanceService.StatePropertyChanged -= StatePersistenceServiceChanged;
        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        DailyBriefingPanelControl.IsOpenChanged -= DailyBriefingPanelIsOpenChanged;
        UnregisterRecipients();

        if (Content is UIElement rootElement)
        {
            rootElement.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
        }

        DetachTitleBarSearchHost();
        DetachShellSynchronizationProvider();
        CloseHostedPopoutWindows();

        if (MainShellFrame.Content is WinoAppShell shellPage)
        {
            shellPage.PrepareForWindowClose();
        }

        Bindings.StopTracking();

        var rootContent = Content;
        WindowCleanupHelper.CleanupObject(rootContent);
        Content = null;
    }

    private void DetachTitleBarSearchHost()
    {
        if (_activeTitleBarSearchHost is IMailTitleBarSearchHost mailHost)
        {
            mailHost.SemanticSearchBusyChanged -= MailHostSemanticSearchBusyChanged;
        }

        _activeTitleBarSearchHost = null;
        TitleBarSearchBox.ItemsSource = null;
        TitleBarSearchBox.SearchHistoryItemsSource = null;
        TitleBarSearchBox.ScopeOptionsSource = null;
        TitleBarSearchBox.ReachOptionsSource = null;
        TitleBarSearchBox.DateOptionsSource = null;
        TitleBarSearchBox.SenderSuggestions = null;
    }

    #region Title bar synchronization

    /// <summary>
    /// Rebinds the title bar's synchronization button to whichever mode is publishing a
    /// menu. The window never learns what any mode synchronizes; it only forwards the
    /// surface <see cref="IShellMenuProvider"/> exposes.
    /// </summary>
    private void ApplyShellSynchronizationProvider()
    {
        var provider = ResolveActiveShellMenuProvider();

        if (!ReferenceEquals(_activeSynchronizationProvider, provider))
        {
            if (_activeSynchronizationProvider != null)
            {
                _activeSynchronizationProvider.PropertyChanged -= ShellSynchronizationProviderPropertyChanged;
            }

            _activeSynchronizationProvider = provider;

            if (_activeSynchronizationProvider != null)
            {
                _activeSynchronizationProvider.PropertyChanged += ShellSynchronizationProviderPropertyChanged;
            }
        }

        RefreshShellSynchronizationButton();
    }

    private IShellMenuProvider? ResolveActiveShellMenuProvider()
        => MainShellFrame.Content is WinoAppShell shellPage ? shellPage.CurrentShellMenuProvider : null;

    private void ShellSynchronizationProviderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activeSynchronizationProvider))
            return;

        if (e.PropertyName is not (nameof(IShellMenuProvider.IsSynchronizationSupported)
            or nameof(IShellMenuProvider.CanSynchronize)
            or nameof(IShellMenuProvider.SynchronizationState)
            or nameof(IShellMenuProvider.SynchronizationDescription)
            or nameof(IShellMenuProvider.SynchronizationToolTip)))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(RefreshShellSynchronizationButton);
    }

    /// <summary>
    /// The provider computes its state from the synchronization manager on every read, so
    /// progress updates only have to nudge the button rather than carry a payload.
    /// </summary>
    public void Receive(AccountSynchronizationProgressUpdatedMessage message)
        => DispatcherQueue.TryEnqueue(RefreshShellSynchronizationButton);

    private void RefreshShellSynchronizationButton()
    {
        var provider = _activeSynchronizationProvider;

        if (provider?.IsSynchronizationSupported != true)
        {
            ShellSynchronizationButton.Visibility = Visibility.Collapsed;
            ShellSynchronizationButton.IsSynchronizing = false;

            // Switching to a mode without synchronization while the pill was out must not
            // leave the title faded behind it.
            SetShellTitleFaded(false);

            return;
        }

        var state = provider.SynchronizationState;

        ShellSynchronizationButton.Visibility = Visibility.Visible;
        ShellSynchronizationButton.IsSynchronizing = state.IsSynchronizing;
        ShellSynchronizationButton.IsIndeterminate = state.IsIndeterminate;
        ShellSynchronizationButton.Progress = state.ProgressPercentage;
        ShellSynchronizationButton.Description = provider.SynchronizationDescription ?? string.Empty;
        ShellSynchronizationButton.IdleToolTip = provider.SynchronizationToolTip ?? string.Empty;

        // A running synchronization leaves the button looking enabled - the click handler
        // is what refuses to restart it - because the disabled visual state dims the glyph,
        // and dimming it would wash out the pill the state is being reported in.
        ShellSynchronizationButton.IsEnabled = provider.CanSynchronize || state.IsSynchronizing;

        // The expanded pill overhangs the title, so the title steps aside while it is out.
        SetShellTitleFaded(state.IsSynchronizing);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShellSynchronizationButton,
            state.IsSynchronizing
                ? provider.SynchronizationDescription ?? string.Empty
                : provider.SynchronizationToolTip ?? string.Empty);
    }

    private async void ShellSynchronizationButtonClicked(object sender, RoutedEventArgs e)
    {
        var provider = _activeSynchronizationProvider;

        if (provider?.IsSynchronizationSupported != true || !provider.CanSynchronize)
            return;

        try
        {
            await provider.SynchronizeAsync();
        }
        catch (Exception exception)
        {
            // Failures are reported by the synchronizers through the shell info bar. The
            // button only has to stop claiming that something is running.
            Serilog.Log.Error(exception, "Title bar synchronization request failed.");
        }
        finally
        {
            RefreshShellSynchronizationButton();
        }
    }

    /// <summary>
    /// Fades the title and subtitle while the synchronization pill is expanded over them.
    /// Opacity, not visibility: their layout has to stay put or the search box slides out of
    /// the middle of the window.
    /// </summary>
    private void SetShellTitleFaded(bool isFaded)
    {
        var targetOpacity = isFaded ? 0f : 1f;

        if (_shellTitleOpacity is not null && Math.Abs(_shellTitleOpacity.Value - targetOpacity) < 0.001f)
            return;

        _shellTitleOpacity = targetOpacity;

        var visual = ElementCompositionPreview.GetElementVisual(ShellTitleHost);
        var compositor = visual.Compositor;

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, targetOpacity);
        animation.Duration = TimeSpan.FromMilliseconds(isFaded ? 140 : 220);

        // Coming back has to wait for the pill to finish collapsing over it.
        if (!isFaded)
        {
            animation.DelayTime = TimeSpan.FromMilliseconds(90);
        }

        visual.StartAnimation("Opacity", animation);
    }

    private void DetachShellSynchronizationProvider()
    {
        if (_activeSynchronizationProvider != null)
        {
            _activeSynchronizationProvider.PropertyChanged -= ShellSynchronizationProviderPropertyChanged;
            _activeSynchronizationProvider = null;
        }
    }

    #endregion

    private static void CloseHostedPopoutWindows()
    {
        var windowManager = WinoApplication.Current.Services.GetService<IWinoWindowManager>();
        var hostedPopouts = windowManager?.GetWindows().OfType<HostedContentPopoutWindow>().ToList() ?? [];

        foreach (var hostedPopout in hostedPopouts)
        {
            hostedPopout.Close();
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        SaveWindowPlacement();

        Closed -= OnWindowClosed;
        AppWindow.Closing -= OnAppWindowClosing;

        // No need to prepare for close or cleanup if the application is exiting, as the process will be terminated shortly after.
        if ((Application.Current as App)?.IsExiting == true)
            return;

        PrepareForClose();
    }

    private void SaveWindowPlacement()
    {
        try
        {
            AppWindow.SaveCurrentPlacement();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to save shell window placement.");
        }
    }

    private async Task<bool> PrepareMailModeForCloseAsync()
    {
        if (MainShellFrame.Content is not WinoAppShell shellPage)
            return true;

        if (shellPage.GetFrame(NavigationReferenceFrame.InnerShellFrame)?.Content is not MailListPage mailListPage)
            return true;

        await mailListPage.ClearMailSelectionAsync();
        WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());

        return true;
    }

    private void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<TitleBarShellContentUpdated>(this);
        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Register<InfoBarMessageRequested>(this);
        WeakReferenceMessenger.Default.Register<WinoAccountProfileUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<WinoAccountProfileDeletedMessage>(this);
        WeakReferenceMessenger.Default.Register<DailyBriefingStateChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceAccessChanged>(this);
        WeakReferenceMessenger.Default.Register<AccountSynchronizationProgressUpdatedMessage>(this);
    }

    private void UnregisterRecipients()
    {
        WeakReferenceMessenger.Default.Unregister<TitleBarShellContentUpdated>(this);
        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Unregister<InfoBarMessageRequested>(this);
        WeakReferenceMessenger.Default.Unregister<WinoAccountProfileUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<WinoAccountProfileDeletedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<DailyBriefingStateChanged>(this);
        WeakReferenceMessenger.Default.Unregister<WinoIntelligenceAccessChanged>(this);
        WeakReferenceMessenger.Default.Unregister<AccountSynchronizationProgressUpdatedMessage>(this);
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

    private void ManageWinoAccountClicked(object sender, RoutedEventArgs e)
    {
        WinoAccountFlyout.Hide();

        // Navigate switches the shell into Settings mode when the target is a settings-only page.
        NavigationService.Navigate(WinoPage.WinoAccountManagementPage);
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
