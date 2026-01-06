using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Services;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class AccountDetailsPageViewModel : MailBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly ICalendarService _calendarService;
    private bool isLoaded = false;

    public MailAccount Account { get; set; }
    public ObservableCollection<IMailItemFolder> CurrentFolders { get; set; } = [];
    public ObservableCollection<AccountCalendar> AccountCalendars { get; set; } = [];

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; } = 1; // Default to Mail tab

    // Mail-related properties
    [ObservableProperty]
    private bool isFocusedInboxEnabled;

    [ObservableProperty]
    private bool areNotificationsEnabled;

    [ObservableProperty]
    private bool isSignatureEnabled;

    [ObservableProperty]
    private bool isAppendMessageSettingVisible;

    [ObservableProperty]
    private bool isAppendMessageSettinEnabled;

    [ObservableProperty]
    private bool isTaskbarBadgeEnabled;

    public bool IsFocusedInboxSupportedForAccount => Account != null && Account.Preferences.IsFocusedInboxEnabled != null;


    public AccountDetailsPageViewModel(IMailDialogService dialogService,
        IAccountService accountService,
        IFolderService folderService,
        ICalendarService calendarService)
    {
        _dialogService = dialogService;
        _accountService = accountService;
        _folderService = folderService;
        _calendarService = calendarService;
    }

    [RelayCommand]
    private Task SetupSpecialFolders()
        => _dialogService.HandleSystemFolderConfigurationDialogAsync(Account.Id, _folderService);

    [RelayCommand]
    private void EditSignature()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsSignature_Title, WinoPage.SignatureManagementPage, Account.Id));

    [RelayCommand]
    private void EditAliases()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsManageAliases_Title, WinoPage.AliasManagementPage, Account.Id));

    public Task FolderSyncToggledAsync(IMailItemFolder folderStructure, bool isEnabled)
        => _folderService.ChangeFolderSynchronizationStateAsync(folderStructure.Id, isEnabled);

    public Task FolderShowUnreadToggled(IMailItemFolder folderStructure, bool isEnabled)
        => _folderService.ChangeFolderShowUnreadCountStateAsync(folderStructure.Id, isEnabled);

    [RelayCommand]
    private void EditAccountDetails()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsEditAccountDetails_Title, WinoPage.EditAccountDetailsPage, Account));

    [RelayCommand]
    private async Task DeleteAccount()
    {
        if (Account == null)
            return;

        var confirmation = await _dialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_DeleteAccountConfirmationTitle,
                                                                           string.Format(Translator.DialogMessage_DeleteAccountConfirmationMessage, Account.Name),
                                                                           Translator.Buttons_Delete);

        if (!confirmation)
            return;


        await SynchronizationManager.Instance.DestroySynchronizerAsync(Account.Id);

        await _accountService.DeleteAccountAsync(Account);

        _dialogService.InfoBarMessage(Translator.Info_AccountDeletedTitle, string.Format(Translator.Info_AccountDeletedMessage, Account.Name), InfoBarMessageType.Success);

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }


    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is Guid accountId)
        {
            Account = await _accountService.GetAccountAsync(accountId);

            IsFocusedInboxEnabled = Account.Preferences.IsFocusedInboxEnabled.GetValueOrDefault();
            AreNotificationsEnabled = Account.Preferences.IsNotificationsEnabled;
            IsSignatureEnabled = Account.Preferences.IsSignatureEnabled;

            IsAppendMessageSettingVisible = Account.ProviderType == MailProviderType.IMAP4;
            IsAppendMessageSettinEnabled = Account.Preferences.ShouldAppendMessagesToSentFolder;
            IsTaskbarBadgeEnabled = Account.Preferences.IsTaskbarBadgeEnabled;

            OnPropertyChanged(nameof(IsFocusedInboxSupportedForAccount));

            var folderStructures = (await _folderService.GetFolderStructureForAccountAsync(Account.Id, true)).Folders;

            foreach (var folder in folderStructures)
            {
                CurrentFolders.Add(folder);
            }

            // Load calendar list
            await LoadAccountCalendarsAsync();

            isLoaded = true;
        }
    }

    private async Task LoadAccountCalendarsAsync()
    {
        var calendars = await _calendarService.GetAccountCalendarsAsync(Account.Id);
        
        AccountCalendars.Clear();
        foreach (var calendar in calendars)
        {
            AccountCalendars.Add(calendar);
        }
    }

    [RelayCommand]
    private void CalendarItemClicked(AccountCalendar calendar)
    {
        if (calendar == null) return;

        // Navigate to calendar settings page with breadcrumb
        Messenger.Send(new BreadcrumbNavigationRequested(calendar.Name, WinoPage.CalendarAccountSettingsPage, calendar));
    }

    protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (!IsActive || !isLoaded) return;

        switch (e.PropertyName)
        {
            case nameof(IsFocusedInboxEnabled) when IsFocusedInboxSupportedForAccount:
                Account.Preferences.IsFocusedInboxEnabled = IsFocusedInboxEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(AreNotificationsEnabled):
                Account.Preferences.IsNotificationsEnabled = AreNotificationsEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(IsAppendMessageSettinEnabled):
                Account.Preferences.ShouldAppendMessagesToSentFolder = IsAppendMessageSettinEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(IsSignatureEnabled):
                Account.Preferences.IsSignatureEnabled = IsSignatureEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(IsTaskbarBadgeEnabled):
                Account.Preferences.IsTaskbarBadgeEnabled = IsTaskbarBadgeEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
        }
    }
}
