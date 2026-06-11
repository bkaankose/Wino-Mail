using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.ViewModels;

namespace Wino.Mail.WinUI.ViewModels;

public sealed partial class WinoAppShellViewModel : CoreBaseViewModel, IShellViewModel
{
    private readonly Dictionary<WinoApplicationMode, IShellClient> _shellClients;
    private readonly IStoreUpdateService _storeUpdateService;
    private readonly IMailDialogService _dialogService;
    private WinoApplicationMode _currentMode;

    public WinoAppShellViewModel(IMailShellClient mailClient,
                                 ICalendarShellClient calendarClient,
                                 IEnumerable<IShellClient> shellClients,
                                 IPreferencesService preferencesService,
                                 IStatePersistanceService statePersistenceService,
                                 INavigationService navigationService,
                                 IStoreUpdateService storeUpdateService,
                                 IMailDialogService dialogService)
    {
        MailClient = mailClient;
        CalendarClient = calendarClient;
        PreferencesService = preferencesService;
        StatePersistenceService = statePersistenceService;
        NavigationService = navigationService;
        _storeUpdateService = storeUpdateService;
        _dialogService = dialogService;

        _shellClients = shellClients.ToDictionary(client => client.Mode);

        foreach (var client in _shellClients.Values)
        {
            client.PropertyChanged += ChildPropertyChanged;
        }

        StatePersistenceService.StatePropertyChanged += StatePersistenceServiceChanged;
    }

    public IMailShellClient MailClient { get; }
    public ICalendarShellClient CalendarClient { get; }
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
        => _shellClients[mode];

    public void SetCurrentMode(WinoApplicationMode mode)
    {
        CurrentMode = mode;
        OnPropertyChanged(nameof(CurrentMenuItems));
    }

    private void ChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, CurrentClient))
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
