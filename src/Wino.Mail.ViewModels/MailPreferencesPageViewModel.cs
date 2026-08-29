using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Mail behavior that applies to every account: where the app starts, how often mail syncs,
/// how searches begin, and the safety nets around sending and deleting.
/// Presentation of the mail list stays on <see cref="MessageListPageViewModel"/>.
/// </summary>
public partial class MailPreferencesPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IProviderService _providerService;

    private bool _isLoaded;
    private int _emailSyncIntervalMinutes;
    private int _undoSendingDraftsIntervalInSeconds;
    private int _undoDeletingMailsIntervalInSeconds;
    private int _selectedMarkAsOptionIndex;
    private string _selectedDefaultSearchMode;

    public MailPreferencesPageViewModel(
        IPreferencesService preferencesService,
        IAccountService accountService,
        IProviderService providerService)
    {
        PreferencesService = preferencesService;
        _accountService = accountService;
        _providerService = providerService;

        SearchModes =
        [
            Translator.SettingsAppPreferences_SearchMode_Local,
            Translator.SettingsAppPreferences_SearchMode_Online,
            Translator.SettingsAppPreferences_SearchMode_Semantic
        ];

        _selectedDefaultSearchMode = SearchModes[(int)PreferencesService.DefaultSearchMode];
        _emailSyncIntervalMinutes = PreferencesService.EmailSyncIntervalMinutes;
        _undoSendingDraftsIntervalInSeconds = PreferencesService.UndoSendingDraftsIntervalInSeconds;
        _undoDeletingMailsIntervalInSeconds = PreferencesService.UndoDeletingMailsIntervalInSeconds;
        _selectedMarkAsOptionIndex = Array.IndexOf(Enum.GetValues<MailMarkAsOption>(), PreferencesService.MarkAsPreference);
    }

    public IPreferencesService PreferencesService { get; }

    public List<string> SearchModes { get; }

    /// <summary>
    /// Accounts and linked inboxes the app can open on launch. Only mailboxes with mail access
    /// qualify, because the startup target is a mail folder.
    /// </summary>
    public ObservableCollection<IAccountProviderDetailViewModel> StartupAccounts { get; } = [];

    [ObservableProperty]
    public partial IAccountProviderDetailViewModel StartupAccount { get; set; }

    public string SelectedDefaultSearchMode
    {
        get => _selectedDefaultSearchMode;
        set
        {
            if (!SetProperty(ref _selectedDefaultSearchMode, value))
                return;

            var searchModeIndex = SearchModes.IndexOf(value);

            if (searchModeIndex >= 0)
            {
                PreferencesService.DefaultSearchMode = (SearchMode)searchModeIndex;
            }
        }
    }

    public int EmailSyncIntervalMinutes
    {
        get => _emailSyncIntervalMinutes;
        set
        {
            if (SetProperty(ref _emailSyncIntervalMinutes, value))
            {
                PreferencesService.EmailSyncIntervalMinutes = value;
            }
        }
    }

    public int UndoSendingDraftsIntervalInSeconds
    {
        get => _undoSendingDraftsIntervalInSeconds;
        set
        {
            if (SetProperty(ref _undoSendingDraftsIntervalInSeconds, value))
            {
                PreferencesService.UndoSendingDraftsIntervalInSeconds = value;
            }
        }
    }

    public int UndoDeletingMailsIntervalInSeconds
    {
        get => _undoDeletingMailsIntervalInSeconds;
        set
        {
            if (SetProperty(ref _undoDeletingMailsIntervalInSeconds, value))
            {
                PreferencesService.UndoDeletingMailsIntervalInSeconds = value;
            }
        }
    }

    public int SelectedMarkAsOptionIndex
    {
        get => _selectedMarkAsOptionIndex;
        set
        {
            if (SetProperty(ref _selectedMarkAsOptionIndex, value) && value >= 0)
            {
                PreferencesService.MarkAsPreference = Enum.GetValues<MailMarkAsOption>()[value];
            }
        }
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        await LoadStartupAccountsAsync();
    }

    partial void OnStartupAccountChanged(IAccountProviderDetailViewModel value)
    {
        if (!_isLoaded || value == null)
            return;

        PreferencesService.StartupEntityId = value.StartupEntityId;
    }

    private async Task LoadStartupAccountsAsync()
    {
        _isLoaded = false;

        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        var startupCandidates = BuildStartupCandidates(accounts);

        await ExecuteUIThread(() =>
        {
            StartupAccounts.Clear();

            foreach (var candidate in startupCandidates)
            {
                StartupAccounts.Add(candidate);
            }

            StartupAccount = PreferencesService.StartupEntityId is { } startupEntityId
                ? StartupAccounts.FirstOrDefault(account => account.StartupEntityId == startupEntityId)
                : null;

            _isLoaded = true;
        });
    }

    /// <summary>
    /// Linked inboxes are offered as a single entry and their members are not listed separately,
    /// mirroring how the shell presents them.
    /// </summary>
    private List<IAccountProviderDetailViewModel> BuildStartupCandidates(IEnumerable<MailAccount> accounts)
    {
        var candidates = new List<IAccountProviderDetailViewModel>();

        var groupedAccounts = accounts
            .OrderBy(account => account.MergedInboxId == null ? 1 : 0)
            .ThenBy(account => account.Order)
            .ThenBy(account => account.Name)
            .GroupBy(account => account.MergedInboxId);

        foreach (var accountGroup in groupedAccounts)
        {
            if (accountGroup.Key == null)
            {
                candidates.AddRange(accountGroup
                    .Where(account => account.IsMailAccessGranted)
                    .Select(CreateAccountDetails));

                continue;
            }

            var holdingAccounts = accountGroup.Select(CreateAccountDetails).ToList();

            if (!holdingAccounts.Any(account => account.Account.IsMailAccessGranted))
                continue;

            candidates.Add(new MergedAccountProviderDetailViewModel(accountGroup.First().MergedInbox, holdingAccounts)
            {
                ProviderDetail = holdingAccounts.FirstOrDefault()?.ProviderDetail
            });
        }

        return candidates;
    }

    private AccountProviderDetailViewModel CreateAccountDetails(MailAccount account)
        => new(_providerService.GetProviderDetail(account.ProviderType), account);
}
