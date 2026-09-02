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
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Backs the per-account Unread badges page: where this account's badges appear, which of its folders
/// feed the account total, and which folders show a badge of their own.
/// </summary>
public partial class AccountUnreadBadgePageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly IUnreadBadgeService _unreadBadgeService;
    private readonly INotificationBuilder _notificationBuilder;

    private MailAccount _account;
    private bool _isLoaded;

    public ObservableCollection<UnreadBadgeFolderViewModel> Folders { get; } = [];

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    public partial string AccountAddress { get; set; }

    [ObservableProperty]
    public partial int AccountUnreadCount { get; set; }

    [ObservableProperty]
    public partial int TaskbarUnreadCount { get; set; }

    [ObservableProperty]
    public partial bool IsAccountBadgeEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsTaskbarBadgeEnabled { get; set; }

    [ObservableProperty]
    public partial bool AreFolderBadgesEnabled { get; set; }

    /// <summary>
    /// Bound to the "Inbox only" radio. The two options are exclusive, so the second one is derived.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedFoldersSource))]
    public partial bool IsInboxOnlySource { get; set; }

    public bool IsSelectedFoldersSource => !IsInboxOnlySource;

    public AccountUnreadBadgePageViewModel(IAccountService accountService,
                                          IFolderService folderService,
                                          IUnreadBadgeService unreadBadgeService,
                                          INotificationBuilder notificationBuilder)
    {
        _accountService = accountService;
        _folderService = folderService;
        _unreadBadgeService = unreadBadgeService;
        _notificationBuilder = notificationBuilder;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not Guid accountId)
            return;

        await LoadAccountAsync(accountId);
    }

    private async Task LoadAccountAsync(Guid accountId)
    {
        _isLoaded = false;

        _account = await _accountService.GetAccountAsync(accountId);

        if (_account == null)
            return;

        AccountName = _account.Name;
        AccountAddress = _account.Address;

        IsAccountBadgeEnabled = _account.Preferences.IsAccountBadgeEnabled;
        IsTaskbarBadgeEnabled = _account.Preferences.IsTaskbarBadgeEnabled;
        AreFolderBadgesEnabled = _account.Preferences.AreFolderBadgesEnabled;
        IsInboxOnlySource = _account.Preferences.UnreadBadgeCountSource == UnreadBadgeCountSource.InboxOnly;

        await LoadFoldersAsync();

        _isLoaded = true;

        await RefreshCountsAsync();
    }

    private async Task LoadFoldersAsync()
    {
        var folders = await _folderService.GetFoldersAsync(_account.Id);

        await ExecuteUIThread(() =>
        {
            Folders.Clear();

            foreach (var folder in folders.Where(folder => folder.IsMoveTarget))
            {
                Folders.Add(new UnreadBadgeFolderViewModel(folder, OnFolderCountedChangedAsync, OnFolderBadgeChangedAsync));
            }
        });
    }

    /// <summary>
    /// Recomputes the two headline numbers from the same snapshot the taskbar uses, then pushes the
    /// per-folder counts back onto the rows.
    /// </summary>
    private async Task RefreshCountsAsync()
    {
        var snapshot = await _unreadBadgeService.GetSnapshotAsync();
        var accountBadge = snapshot.GetAccount(_account.Id);

        var folderCounts = new Dictionary<Guid, int>();

        foreach (var folder in Folders)
        {
            folderCounts[folder.FolderId] = await _folderService.GetFolderUnreadCountAsync(folder.FolderId);
        }

        await ExecuteUIThread(() =>
        {
            AccountUnreadCount = accountBadge?.UnreadCount ?? 0;
            TaskbarUnreadCount = snapshot.TaskbarUnreadCount;

            var isInboxOnly = IsInboxOnlySource;

            foreach (var folder in Folders)
            {
                folder.UnreadCount = folderCounts.TryGetValue(folder.FolderId, out var count) ? count : 0;

                // Inbox only counts the Inbox whatever the per-folder flags say, so the column shows that
                // rather than the stored selection. The selection is kept for the switch back.
                folder.ApplyCountedState(
                    isCounted: isInboxOnly ? folder.SpecialFolderType == SpecialFolderType.Inbox : folder.StoredIsCounted,
                    isEditable: !isInboxOnly);

                folder.IsBadgeEditable = AreFolderBadgesEnabled;
            }
        });
    }

    private async Task OnFolderCountedChangedAsync(UnreadBadgeFolderViewModel folder)
    {
        if (!_isLoaded) return;

        folder.StoredIsCounted = folder.IsCounted;

        await _folderService.ChangeFolderCountedInAccountTotalStateAsync(folder.FolderId, folder.IsCounted);
        await RefreshBadgesAsync();
    }

    private async Task OnFolderBadgeChangedAsync(UnreadBadgeFolderViewModel folder)
    {
        if (!_isLoaded) return;

        // Badge visibility never changes a total, so only the navigation needs refreshing.
        await _folderService.ChangeFolderShowUnreadCountStateAsync(folder.FolderId, folder.ShowBadge);
        RequestUnreadCountRefresh();
    }

    protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (!_isLoaded || _account == null) return;

        switch (e.PropertyName)
        {
            case nameof(IsAccountBadgeEnabled):
                _account.Preferences.IsAccountBadgeEnabled = IsAccountBadgeEnabled;
                await SaveAndRefreshAsync();
                break;
            case nameof(IsTaskbarBadgeEnabled):
                _account.Preferences.IsTaskbarBadgeEnabled = IsTaskbarBadgeEnabled;
                await SaveAndRefreshAsync();
                break;
            case nameof(AreFolderBadgesEnabled):
                _account.Preferences.AreFolderBadgesEnabled = AreFolderBadgesEnabled;
                await SaveAndRefreshAsync();
                break;
            case nameof(IsInboxOnlySource):
                _account.Preferences.UnreadBadgeCountSource = IsInboxOnlySource
                    ? UnreadBadgeCountSource.InboxOnly
                    : UnreadBadgeCountSource.SelectedFolders;
                await SaveAndRefreshAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (_account == null) return;

        _isLoaded = false;

        _account.Preferences.IsAccountBadgeEnabled = true;
        _account.Preferences.IsTaskbarBadgeEnabled = true;
        _account.Preferences.AreFolderBadgesEnabled = true;
        _account.Preferences.UnreadBadgeCountSource = UnreadBadgeCountSource.InboxOnly;

        IsAccountBadgeEnabled = true;
        IsTaskbarBadgeEnabled = true;
        AreFolderBadgesEnabled = true;
        IsInboxOnlySource = true;

        _isLoaded = true;

        await SaveAndRefreshAsync();
    }

    private async Task SaveAndRefreshAsync()
    {
        await _accountService.UpdateAccountAsync(_account);
        await RefreshBadgesAsync();
    }

    private async Task RefreshBadgesAsync()
    {
        await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
        await RefreshCountsAsync();

        RequestUnreadCountRefresh();
    }

    private void RequestUnreadCountRefresh()
        => Messenger.Send(new RefreshUnreadCountsMessage(_account.Id));
}

/// <summary>
/// One folder row: what it holds, whether it feeds the account total, and whether it shows its own badge.
/// </summary>
public partial class UnreadBadgeFolderViewModel : ObservableObject
{
    private readonly Func<UnreadBadgeFolderViewModel, Task> _countedChanged;
    private readonly Func<UnreadBadgeFolderViewModel, Task> _badgeChanged;
    private bool _isInitialized;
    private bool _isApplyingState;

    /// <summary>
    /// The user's own selection for this folder, kept while "Inbox only" overrides what the column shows.
    /// </summary>
    public bool StoredIsCounted { get; set; }

    public Guid FolderId { get; }
    public string FolderName { get; }
    public SpecialFolderType SpecialFolderType { get; }

    /// <summary>
    /// Draft and Junk show how many items they hold rather than how many are unread, so the row has to
    /// label the number differently.
    /// </summary>
    public bool IsItemCountFolder
        => SpecialFolderType == SpecialFolderType.Draft || SpecialFolderType == SpecialFolderType.Junk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountDescription))]
    public partial int UnreadCount { get; set; }

    /// <summary>
    /// Draft and Junk are labelled as items because that is what Wino counts for them.
    /// </summary>
    public string CountDescription
        => IsItemCountFolder
            ? string.Format(Translator.UnreadBadges_Folders_ItemsFormat, UnreadCount)
            : string.Format(Translator.UnreadBadges_Folders_UnreadFormat, UnreadCount);

    [ObservableProperty]
    public partial bool IsCounted { get; set; }

    [ObservableProperty]
    public partial bool ShowBadge { get; set; }

    [ObservableProperty]
    public partial bool IsCountedEditable { get; set; }

    [ObservableProperty]
    public partial bool IsBadgeEditable { get; set; }

    public UnreadBadgeFolderViewModel(IMailItemFolder folder,
                                      Func<UnreadBadgeFolderViewModel, Task> countedChanged,
                                      Func<UnreadBadgeFolderViewModel, Task> badgeChanged)
    {
        FolderId = folder.Id;
        FolderName = folder.FolderName;
        SpecialFolderType = folder.SpecialFolderType;

        StoredIsCounted = folder.IsCountedInAccountTotal;
        IsCounted = folder.IsCountedInAccountTotal;
        ShowBadge = folder.ShowUnreadCount;

        _countedChanged = countedChanged;
        _badgeChanged = badgeChanged;
        _isInitialized = true;
    }

    /// <summary>
    /// Sets what the Count column shows without treating it as a user edit.
    /// </summary>
    public void ApplyCountedState(bool isCounted, bool isEditable)
    {
        _isApplyingState = true;

        IsCounted = isCounted;
        IsCountedEditable = isEditable;

        _isApplyingState = false;
    }

    partial void OnIsCountedChanged(bool value)
    {
        if (!_isInitialized || _isApplyingState) return;

        _ = _countedChanged(this);
    }

    partial void OnShowBadgeChanged(bool value)
    {
        if (!_isInitialized) return;

        _ = _badgeChanged(this);
    }
}
