using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.ViewModels;

namespace Wino.Mail.WinUI.ViewModels;

public sealed partial class WinoAppShellViewModel : CoreBaseViewModel, IShellViewModel
{
    private readonly Dictionary<WinoApplicationMode, IShellClient> _shellClients = [];
    private readonly IServiceProvider _serviceProvider;
    private readonly IStoreUpdateService _storeUpdateService;
    private readonly IMailDialogService _dialogService;
    private WinoApplicationMode _currentMode;

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

    public IMailShellClient MailClient => (IMailShellClient)GetClient(WinoApplicationMode.Mail);
    public ICalendarShellClient CalendarClient => (ICalendarShellClient)GetClient(WinoApplicationMode.Calendar);
    public IEnumerable<IShellClient> InitializedClients => _shellClients.Values;
    public IPreferencesService PreferencesService { get; }
    public IStatePersistanceService StatePersistenceService { get; }
    public INavigationService NavigationService { get; }

    public WinoApplicationMode CurrentMode
    {
        get => _currentMode;
        private set
        {
            if (SetProperty(ref _currentMode, value))
            {
                OnPropertyChanged(nameof(CurrentClient));
                OnPropertyChanged(nameof(CurrentMenuItems));
                OnPropertyChanged(nameof(IsMailMode));
                OnPropertyChanged(nameof(IsCalendarMode));
                OnPropertyChanged(nameof(IsContactsMode));
                OnPropertyChanged(nameof(IsSettingsMode));
                OnPropertyChanged(nameof(SelectedMenuItem));
            }
        }
    }

    public IShellClient CurrentClient => GetClient(CurrentMode);
    public bool IsMailMode => CurrentMode == WinoApplicationMode.Mail;
    public bool IsCalendarMode => CurrentMode == WinoApplicationMode.Calendar;
    public bool IsContactsMode => CurrentMode == WinoApplicationMode.Contacts;
    public bool IsSettingsMode => CurrentMode == WinoApplicationMode.Settings;
    public MenuItemCollection? CurrentMenuItems => CurrentClient.MenuItems;

    public object? SelectedMenuItem
    {
        get => CurrentClient.SelectedMenuItem;
        set
        {
            if (!ReferenceEquals(CurrentClient.SelectedMenuItem, value))
            {
                CurrentClient.SelectedMenuItem = value;
                OnPropertyChanged();
            }
        }
    }

    public override void OnNavigatedTo(Core.Domain.Models.Navigation.NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        CurrentMode = StatePersistenceService.ApplicationMode;
        _ = ShowStoreUpdateDialogIfNeededAsync();
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

    public IShellClient GetClient(WinoApplicationMode mode)
    {
        if (_shellClients.TryGetValue(mode, out var client))
            return client;

        client = mode switch
        {
            WinoApplicationMode.Mail => _serviceProvider.GetRequiredService<IMailShellClient>(),
            WinoApplicationMode.Calendar => _serviceProvider.GetRequiredService<ICalendarShellClient>(),
            WinoApplicationMode.Contacts => _serviceProvider.GetRequiredService<ContactsShellClient>(),
            WinoApplicationMode.Settings => _serviceProvider.GetRequiredService<SettingsShellClient>(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        _shellClients.Add(mode, client);
        client.PropertyChanged += ChildPropertyChanged;
        return client;
    }

    public bool TryGetClient(WinoApplicationMode mode, out IShellClient? client)
        => _shellClients.TryGetValue(mode, out client);

    public void SetCurrentMode(WinoApplicationMode mode)
    {
        CurrentMode = mode;
        OnPropertyChanged(nameof(CurrentMenuItems));
    }

    private void ChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (TryGetClient(CurrentMode, out var currentClient) && ReferenceEquals(sender, currentClient))
        {
            if (e.PropertyName == nameof(IShellClient.SelectedMenuItem))
            {
                OnPropertyChanged(nameof(SelectedMenuItem));
            }

            if (e.PropertyName == nameof(IShellClient.MenuItems))
            {
                OnPropertyChanged(nameof(CurrentMenuItems));
            }
        }
    }

    private void StatePersistenceServiceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IStatePersistanceService.ApplicationMode))
        {
            SetCurrentMode(StatePersistenceService.ApplicationMode);
        }
    }
}
