using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Backs the application level Unread badges page: which accounts feed the taskbar badge and whether
/// a taskbar launch follows the badge instead of the configured startup item.
/// </summary>
public partial class UnreadBadgeSettingsPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IUnreadBadgeService _unreadBadgeService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IPreferencesService _preferencesService;

    private bool _isLoaded;

    public ObservableCollection<TaskbarBadgeAccountViewModel> Accounts { get; } = [];

    [ObservableProperty]
    public partial bool IsLaunchNavigationEnabled { get; set; }

    public UnreadBadgeSettingsPageViewModel(IAccountService accountService,
                                            IUnreadBadgeService unreadBadgeService,
                                            INotificationBuilder notificationBuilder,
                                            IPreferencesService preferencesService)
    {
        _accountService = accountService;
        _unreadBadgeService = unreadBadgeService;
        _notificationBuilder = notificationBuilder;
        _preferencesService = preferencesService;

        IsLaunchNavigationEnabled = _preferencesService.IsTaskbarBadgeLaunchNavigationEnabled;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        await LoadAccountsAsync();
    }

    private async Task LoadAccountsAsync()
    {
        _isLoaded = false;

        var accounts = await _accountService.GetAccountsAsync();
        var snapshot = await _unreadBadgeService.GetSnapshotAsync();

        await ExecuteUIThread(() =>
        {
            Accounts.Clear();

            foreach (var account in accounts)
            {
                if (!account.IsMailAccessGranted)
                    continue;

                var badge = snapshot.GetAccount(account.Id);

                Accounts.Add(new TaskbarBadgeAccountViewModel(
                    account,
                    badge?.UnreadCount ?? 0,
                    account.Preferences.IsTaskbarBadgeEnabled,
                    GetCountSourceDescription(account.Preferences.UnreadBadgeCountSource),
                    OnAccountContributionChangedAsync));
            }
        });

        _isLoaded = true;
    }

    private static string GetCountSourceDescription(UnreadBadgeCountSource countSource)
        => countSource == UnreadBadgeCountSource.InboxOnly
            ? Translator.UnreadBadges_CountSource_InboxOnly_Title
            : Translator.UnreadBadges_CountSource_SelectedFolders_Title;

    private async Task OnAccountContributionChangedAsync(TaskbarBadgeAccountViewModel accountViewModel)
    {
        if (!_isLoaded) return;

        var account = await _accountService.GetAccountAsync(accountViewModel.AccountId);

        if (account == null) return;

        account.Preferences.IsTaskbarBadgeEnabled = accountViewModel.ContributesToTaskbar;

        await _accountService.UpdateAccountAsync(account);
        await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsLaunchNavigationEnabled))
        {
            _preferencesService.IsTaskbarBadgeLaunchNavigationEnabled = IsLaunchNavigationEnabled;
        }
    }

    [RelayCommand]
    private void ConfigureAccount(TaskbarBadgeAccountViewModel accountViewModel)
    {
        if (accountViewModel == null) return;

        Messenger.Send(new BreadcrumbNavigationRequested(
            string.Format(Translator.UnreadBadges_AccountSettingsTitleFormat, accountViewModel.AccountName),
            WinoPage.AccountUnreadBadgePage,
            accountViewModel.AccountId));
    }
}

/// <summary>
/// One account row on the hub: what it currently contributes and whether it is allowed to.
/// </summary>
public partial class TaskbarBadgeAccountViewModel : ObservableObject
{
    private readonly Func<TaskbarBadgeAccountViewModel, Task> _contributionChanged;
    private readonly bool _isInitialized;

    public MailAccount Account { get; }
    public Guid AccountId => Account.Id;
    public string AccountName => Account.Name;
    public string DescriptionText => string.IsNullOrWhiteSpace(Account.Address)
        ? CapabilitySummary
        : $"{CapabilitySummary} | {Account.Address}";
    public int UnreadCount { get; }
    public string CountSourceDescription { get; }
    private string CapabilitySummary => Account.IsCalendarAccessGranted
        ? Translator.AccountCapability_MailAndCalendar
        : Translator.AccountCapability_MailOnly;

    [ObservableProperty]
    public partial bool ContributesToTaskbar { get; set; }

    public TaskbarBadgeAccountViewModel(MailAccount account,
                                        int unreadCount,
                                        bool contributesToTaskbar,
                                        string countSourceDescription,
                                        Func<TaskbarBadgeAccountViewModel, Task> contributionChanged)
    {
        Account = account;
        UnreadCount = unreadCount;
        CountSourceDescription = countSourceDescription;
        ContributesToTaskbar = contributesToTaskbar;

        _contributionChanged = contributionChanged;
        _isInitialized = true;
    }

    partial void OnContributesToTaskbarChanged(bool value)
    {
        if (!_isInitialized) return;

        _ = _contributionChanged(this);
    }
}
