#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Calendar.ViewModels;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels;

namespace Wino.Mail.WinUI.ViewModels;

/// <summary>
/// Hosts whatever navigation menu the currently navigated page published. It deliberately
/// knows nothing about the pages themselves; every menu item, command and template comes
/// from the mode that owns it.
/// </summary>
public sealed partial class WinoAppShellViewModel : CoreBaseViewModel, IShellViewModel
{
    private readonly Dictionary<WinoApplicationMode, IShellMenuProvider> _providers = [];
    private readonly IServiceProvider _serviceProvider;
    private readonly IStoreUpdateService _storeUpdateService;
    private readonly IMailDialogService _dialogService;

    private WinoApplicationMode _currentMode;
    private ShellMenu? _currentMenu;
    private IShellMenuProvider? _currentProvider;
    private bool _isPaneCompact;
    private bool _isShutdown;

    public WinoAppShellViewModel(IServiceProvider serviceProvider,
                                 IPreferencesService preferencesService,
                                 IStatePersistanceService statePersistenceService,
                                 INavigationService navigationService,
                                 IStoreUpdateService storeUpdateService,
                                 IMailDialogService dialogService)
    {
        _serviceProvider = serviceProvider;
        PreferencesService = preferencesService;
        StatePersistenceService = statePersistenceService;
        NavigationService = navigationService;
        _storeUpdateService = storeUpdateService;
        _dialogService = dialogService;

        StatePersistenceService.StatePropertyChanged += StatePersistenceServiceChanged;
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

    public object? SelectedMenuItem
    {
        get => _currentProvider?.SelectedMenuItem;
        set
        {
            if (_currentProvider == null || ReferenceEquals(_currentProvider.SelectedMenuItem, value))
                return;

            _currentProvider.SelectedMenuItem = value;
            OnPropertyChanged();
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

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        CurrentMode = StatePersistenceService.ApplicationMode;
        _ = ShowStoreUpdateDialogIfNeededAsync();
    }

    public IShellMenuProvider GetProvider(WinoApplicationMode mode)
    {
        if (_providers.TryGetValue(mode, out var provider))
            return provider;

        // Modes are resolved the first time they are visited, so never opening the calendar
        // never builds the calendar object graph.
        provider = mode switch
        {
            WinoApplicationMode.Mail => _serviceProvider.GetRequiredService<IMailShellClient>(),
            WinoApplicationMode.Calendar => _serviceProvider.GetRequiredService<ICalendarShellClient>(),
            WinoApplicationMode.Contacts => _serviceProvider.GetRequiredService<ContactsPageViewModel>(),
            WinoApplicationMode.Tasks => _serviceProvider.GetRequiredService<ToDoPageViewModel>(),
            WinoApplicationMode.Settings => _serviceProvider.GetRequiredService<SettingsMenuProvider>(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

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

    private async Task ShowStoreUpdateDialogIfNeededAsync()
    {
        if (!PreferencesService.IsStoreUpdateNotificationsEnabled)
            return;

        var hasAvailableUpdate = await _storeUpdateService.RefreshAvailabilityAsync();

        if (!hasAvailableUpdate || !PreferencesService.IsStoreUpdateNotificationsEnabled)
            return;

        var shouldUpdate = await _dialogService.ShowWinoCustomMessageDialogAsync(
            Translator.Notifications_StoreUpdateAvailableTitle,
            Translator.Notifications_StoreUpdateAvailableMessage,
            Translator.Buttons_Update,
            WinoCustomMessageDialogIcon.Information,
            Translator.Buttons_NotNow,
            Constants.StoreUpdateNotificationSuppressionKey);

        if (shouldUpdate)
        {
            await _storeUpdateService.StartUpdateAsync();
        }
    }
}
