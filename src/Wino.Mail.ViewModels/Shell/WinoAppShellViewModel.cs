#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Calendar.ViewModels;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels;

namespace Wino.Shell.ViewModels;

/// <summary>
/// Hosts whatever navigation menu the currently navigated page published. It deliberately
/// knows nothing about the pages themselves; every menu item, command and template comes
/// from the mode that owns it.
/// </summary>
public sealed partial class WinoAppShellViewModel : CoreBaseViewModel, IShellViewModel
{
    private readonly Dictionary<WinoApplicationMode, IShellMenuProvider> _providers = [];
    private bool _isApplyingSelection;
    private readonly IShellMenuProviderResolver _providerResolver;
    private readonly IMicrosoftStoreService _storeService;
    private readonly IMailDialogService _dialogService;
    private readonly IWinoLogger _logger;
    private readonly IPlatformCapabilities _platformCapabilities;
    private readonly IAppModeReadinessService _appModeReadinessService;
    private bool _isCheckingStoreUpdate;
    private bool _hasShownStoreUpdatePrompt;

    [ObservableProperty]
    public partial bool IsStoreUpdateCardVisible { get; set; }

    [ObservableProperty]
    public partial string StoreUpdateCardMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStoreUpdateDontAskAgainChecked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStoreUpdateIdle))]
    public partial bool IsStoreUpdateInstalling { get; set; }

    public bool IsStoreUpdateIdle => !IsStoreUpdateInstalling;

    /// <summary>
    /// Download and install progress from 0 to 100. Indeterminate until the first report arrives.
    /// </summary>
    [ObservableProperty]
    public partial double StoreUpdateProgress { get; set; }

    [ObservableProperty]
    public partial bool IsStoreUpdateProgressIndeterminate { get; set; }
    private int _modeVisibilityRequestId;

    private WinoApplicationMode _currentMode;
    private AppModeVisibility? _modeVisibility;
    private ShellMenu? _currentMenu;
    private IShellMenuProvider? _currentProvider;
    private bool _isPaneCompact;
    private bool _isShutdown;

    public WinoAppShellViewModel(IShellMenuProviderResolver providerResolver,
                                 IPreferencesService preferencesService,
                                 IStatePersistanceService statePersistenceService,
                                 INavigationService navigationService,
                                 IMicrosoftStoreService storeService,
                                 IMailDialogService dialogService,
                                 IWinoLogger logger,
                                 IPlatformCapabilities platformCapabilities,
                                 IAppModeReadinessService appModeReadinessService,
                                 IRelayHealth? relayHealth = null)
    {
        _relayHealth = relayHealth;
        if (_relayHealth is not null)
        {
            _relayHealth.Changed += RelayHealthChanged;
            IsRelayFallbackActive = _relayHealth.IsFallbackActive;
        }

        _providerResolver = providerResolver;
        PreferencesService = preferencesService;
        StatePersistenceService = statePersistenceService;
        NavigationService = navigationService;
        _storeService = storeService;
        _dialogService = dialogService;
        _logger = logger;
        _platformCapabilities = platformCapabilities;
        _appModeReadinessService = appModeReadinessService;

        StatePersistenceService.StatePropertyChanged += StatePersistenceServiceChanged;
        _appModeReadinessService.ReadinessInvalidated += ReadinessInvalidated;
    }

    private readonly IRelayHealth? _relayHealth;

    /// <summary>
    /// Relay-based sync is selected but the relay socket is down, so mail and calendar run on the 5 minute fallback
    /// timer. The shell shows its red dot only then; a healthy relay shows nothing.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRelayFallbackActive { get; set; }

    private void RelayHealthChanged(object? sender, EventArgs e)
    {
        var active = _relayHealth?.IsFallbackActive == true;
        _ = ExecuteUIThread(() => IsRelayFallbackActive = active);
    }

    public IPreferencesService PreferencesService { get; }
    public IStatePersistanceService StatePersistenceService { get; }
    public INavigationService NavigationService { get; }

    public WinoApplicationMode CurrentMode
    {
        get => _currentMode;
        private set => SetProperty(ref _currentMode, value);
    }

    /// <summary>
    /// The modes the switcher offers right now. Null until the first evaluation completes.
    /// </summary>
    public AppModeVisibility? ModeVisibility
    {
        get => _modeVisibility;
        private set => SetProperty(ref _modeVisibility, value);
    }

    /// <summary>
    /// The single binding target for the navigation view. Null while a mode switch is in
    /// flight, which is what releases the navigation view's item containers.
    /// </summary>
    public ShellMenu? CurrentMenu
    {
        get => _currentMenu;
        private set
        {
            if (SetProperty(ref _currentMenu, value))
            {
                OnPropertyChanged(nameof(SelectedMenuItem));
                OnPropertyChanged(nameof(HandlesSelection));
            }
        }
    }

    public bool HandlesSelection => CurrentMenu?.HandlesSelection == true;

    /// <summary>
    /// The mode currently publishing a menu. The shell window reads its synchronization
    /// surface for the title bar button; nothing else here looks at it.
    /// </summary>
    public IShellMenuProvider? CurrentProvider => _currentProvider;

    public object? SelectedMenuItem
    {
        get => _currentProvider?.SelectedMenuItem;
        set
        {
            if (_isApplyingSelection || _currentProvider == null || ReferenceEquals(_currentProvider.SelectedMenuItem, value))
                return;

            // A provider may keep its own selection, and the navigation view may be unable
            // to show it (the item is not in the pane). Each then reports back to the other;
            // without this guard that exchange never ends and overflows the stack.
            _isApplyingSelection = true;

            try
            {
                _currentProvider.SelectedMenuItem = value;
                OnPropertyChanged();
            }
            finally
            {
                _isApplyingSelection = false;
            }
        }
    }

    /// <summary>
    /// Called by the navigation service once the inner frame lands on a page that owns a
    /// menu. Passing null clears the pane ahead of a mode switch.
    /// </summary>
    public void SetShellMenu(IShellMenuProvider? provider)
    {
        if (ReferenceEquals(_currentProvider, provider))
        {
            // Same provider, but its menu instance may have been rebuilt.
            CurrentMenu = provider?.ShellMenu;
            return;
        }

        if (_currentProvider != null)
        {
            _currentProvider.PropertyChanged -= ProviderPropertyChanged;
        }

        _currentProvider = provider;

        if (_currentProvider != null)
        {
            _currentProvider.PropertyChanged += ProviderPropertyChanged;
        }

        // A newly published menu has to be told the pane state it is arriving into.
        provider?.SetPaneCompact(_isPaneCompact);

        CurrentMenu = provider?.ShellMenu;
        OnPropertyChanged(nameof(SelectedMenuItem));
        OnPropertyChanged(nameof(CurrentProvider));
    }

    /// <summary>
    /// Reports the pane's own width state to whichever mode is showing. The shell does not
    /// know or care which entries that hides.
    /// </summary>
    public void SetPaneCompact(bool isCompact)
    {
        if (_isPaneCompact == isCompact)
            return;

        _isPaneCompact = isCompact;
        _currentProvider?.SetPaneCompact(isCompact);
    }

    public Task InvokeMenuItemAsync(IMenuItem? menuItem)
        => _currentProvider?.OnMenuItemInvokedAsync(menuItem) ?? Task.CompletedTask;

    public Task ChangeMenuSelectionAsync(IMenuItem? menuItem)
        => _currentProvider?.OnMenuSelectionChangedAsync(menuItem) ?? Task.CompletedTask;

    public Task KeyboardShortcutHookForMode(KeyboardShortcutTriggerDetails details)
        => _currentProvider?.KeyboardShortcutHook(details) ?? Task.CompletedTask;

    public override void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        CurrentMode = StatePersistenceService.ApplicationMode;
        _ = RefreshModeVisibilityAsync();
        _ = ShowStoreUpdateCardIfNeededAsync();
    }

    public IShellMenuProvider GetProvider(WinoApplicationMode mode)
    {
        if (_providers.TryGetValue(mode, out var provider))
            return provider;

        // Modes are resolved the first time they are visited, so never opening the calendar
        // never builds the calendar object graph.
        provider = _providerResolver.Resolve(mode);

        _providers.Add(mode, provider);
        return provider;
    }

    public bool TryGetProvider(WinoApplicationMode mode, out IShellMenuProvider? provider)
        => _providers.TryGetValue(mode, out provider);

    public void SetCurrentMode(WinoApplicationMode mode) => CurrentMode = mode;

    /// <summary>
    /// Window teardown. Every mode that was ever opened releases its menu and its
    /// long-lived subscriptions.
    /// </summary>
    public void ShutdownProviders()
    {
        if (_isShutdown)
            return;

        _isShutdown = true;
        SetShellMenu(null);

        foreach (var provider in _providers.Values)
        {
            switch (provider)
            {
                case ContactsPageViewModel contactsProvider:
                    contactsProvider.PrepareForShellShutdown();
                    break;
                case SettingsMenuProvider settingsProvider:
                    settingsProvider.PrepareForShellShutdown();
                    break;
                case MailAppShellViewModel mailProvider:
                    mailProvider.PrepareForShellShutdown();
                    break;
                case CalendarAppShellViewModel calendarProvider:
                    calendarProvider.PrepareForShellShutdown();
                    break;
                case ToDoPageViewModel tasksProvider:
                    tasksProvider.PrepareForShellShutdown();
                    break;
                default:
                    provider.ReleaseShellMenu();
                    break;
            }

            // Providers are application singletons. Do not let them retain the dispatcher
            // wrapper created by this window after all window-owned state is released.
            provider.Dispatcher = null!;
        }

        _providers.Clear();
        StatePersistenceService.StatePropertyChanged -= StatePersistenceServiceChanged;
        _appModeReadinessService.ReadinessInvalidated -= ReadinessInvalidated;
    }

    private void ReadinessInvalidated(object? sender, EventArgs e) => _ = RefreshModeVisibilityAsync();

    private async Task RefreshModeVisibilityAsync()
    {
        if (_isShutdown)
            return;

        var requestId = Interlocked.Increment(ref _modeVisibilityRequestId);

        try
        {
            var visibility = await _appModeReadinessService.GetVisibilityAsync();

            if (_isShutdown || requestId != _modeVisibilityRequestId)
                return;

            await ExecuteUIThreadAsync(() =>
            {
                if (_isShutdown || requestId != _modeVisibilityRequestId)
                    return Task.CompletedTask;

                ModeVisibility = visibility;
                LeaveHiddenMode(visibility);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, nameof(RefreshModeVisibilityAsync));
        }
    }

    /// <summary>
    /// The mode on screen can disappear underneath the user: its last account removed, the
    /// feature turned off, or the mode hidden in Settings. The shell moves to the first visible
    /// mode and says why, instead of leaving a mode the switcher no longer shows.
    /// </summary>
    private void LeaveHiddenMode(AppModeVisibility visibility)
    {
        var currentMode = StatePersistenceService.ApplicationMode;

        if (visibility.IsVisible(currentMode))
            return;

        var fallbackMode = visibility.FallbackMode;

        if (!NavigationService.ChangeApplicationMode(fallbackMode))
            return;

        _dialogService.InfoBarMessage(
            string.Format(Translator.AppModeSwitcher_ModeUnavailableTitle, AppModeOptionViewModel.GetDisplayName(currentMode)),
            string.Format(Translator.AppModeSwitcher_ModeUnavailableMessage, AppModeOptionViewModel.GetDisplayName(fallbackMode)),
            InfoBarMessageType.Information);
    }

    private void ProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _currentProvider))
            return;

        if (e.PropertyName == nameof(IShellMenuProvider.SelectedMenuItem))
        {
            OnPropertyChanged(nameof(SelectedMenuItem));
        }
        else if (e.PropertyName == nameof(IShellMenuProvider.ShellMenu))
        {
            CurrentMenu = _currentProvider?.ShellMenu;
        }
    }

    private void StatePersistenceServiceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IStatePersistanceService.ApplicationMode))
        {
            SetCurrentMode(StatePersistenceService.ApplicationMode);
        }
    }

    private async Task ShowStoreUpdateCardIfNeededAsync()
    {
        if (!_platformCapabilities.MicrosoftStore || _isShutdown || _isCheckingStoreUpdate || _hasShownStoreUpdatePrompt ||
            !PreferencesService.IsStoreUpdateNotificationsEnabled)
            return;

        _isCheckingStoreUpdate = true;

        try
        {
            var hasAvailableUpdate = await _storeService.RefreshAvailabilityAsync();

            if (_isShutdown || !hasAvailableUpdate || !PreferencesService.IsStoreUpdateNotificationsEnabled)
                return;

            var version = _storeService.AvailableUpdateVersion;

            await ExecuteUIThreadAsync(() =>
            {
                if (_isShutdown || !PreferencesService.IsStoreUpdateNotificationsEnabled)
                    return Task.CompletedTask;

                _hasShownStoreUpdatePrompt = true;
                // Only Store updates report a version; App Installer feeds do not.
                StoreUpdateCardMessage = string.IsNullOrEmpty(version)
                    ? Translator.Notifications_AppUpdateAvailableMessage
                    : string.Format(Translator.Notifications_StoreUpdateAvailableVersionMessage, version);
                IsStoreUpdateDontAskAgainChecked = false;
                IsStoreUpdateCardVisible = true;

                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, nameof(ShowStoreUpdateCardIfNeededAsync));
        }
        finally
        {
            _isCheckingStoreUpdate = false;
        }
    }

    [RelayCommand]
    private async Task InstallStoreUpdateAsync()
    {
        if (_isShutdown || IsStoreUpdateInstalling)
            return;

        IsStoreUpdateInstalling = true;
        IsStoreUpdateProgressIndeterminate = true;
        StoreUpdateProgress = 0;
        StoreUpdateCardMessage = Translator.Notifications_AppUpdateInstallingMessage;

        var progress = new Progress<double>(value =>
        {
            IsStoreUpdateProgressIndeterminate = false;
            StoreUpdateProgress = value;
        });

        var isInstalled = false;

        try
        {
            isInstalled = await _storeService.StartUpdateAsync(progress);
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, nameof(InstallStoreUpdateAsync));
        }

        // A successful install closes the app, so reaching here usually means nothing was installed.
        if (_isShutdown || isInstalled)
            return;

        IsStoreUpdateInstalling = false;
        StoreUpdateCardMessage = Translator.Notifications_AppUpdateFailedMessage;
    }

    [RelayCommand]
    private void DismissStoreUpdateCard() => CloseStoreUpdateCard();

    private void CloseStoreUpdateCard()
    {
        IsStoreUpdateCardVisible = false;

        if (IsStoreUpdateDontAskAgainChecked)
        {
            PreferencesService.IsStoreUpdateNotificationsEnabled = false;
        }
    }
}
