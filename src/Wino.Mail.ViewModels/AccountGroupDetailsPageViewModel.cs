using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Creates or edits an account group: its name, color, icon, whether its folders are combined, and its ordered
/// accounts. Moving accounts between groups happens only here.
/// </summary>
public partial class AccountGroupDetailsPageViewModel : CoreBaseViewModel
{
    private const int MinimumMemberCount = 2;

    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;

    private MergedInbox? _group;

    public AccountGroupDetailsPageViewModel(IMailDialogService dialogService, IAccountService accountService)
    {
        _dialogService = dialogService;
        _accountService = accountService;

        MemberEntries.CollectionChanged += MemberEntriesChanged;
    }

    public AccountGroupColorOption[] ColorOptions { get; } =
        [new AccountGroupColorOption(null), .. AccountGroupColors.All.Select(color => new AccountGroupColorOption(color))];

    public AccountGroupIconOption[] IconOptions { get; } = AccountGroupIcons.All.Select(icon => new AccountGroupIconOption(icon)).ToArray();

    public ObservableCollection<AccountGroupEntryViewModel> MemberEntries { get; } = [];

    public ObservableCollection<AccountGroupEntryViewModel> AvailableEntries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNameMissing))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string GroupName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedColorKey))]
    public partial AccountGroupColorOption? SelectedColorOption { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedIconGlyph))]
    public partial AccountGroupIconOption? SelectedIconOption { get; set; }

    [ObservableProperty]
    public partial bool CombineFolders { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExistingGroup))]
    [NotifyCanExecuteChangedFor(nameof(DeleteGroupCommand))]
    public partial bool IsNewGroup { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteGroupCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsExistingGroup => !IsNewGroup;

    public bool IsNameMissing => string.IsNullOrWhiteSpace(GroupName);

    public bool IsMemberMissing => MemberEntries.Count < MinimumMemberCount;

    public bool HasAvailableEntries => AvailableEntries.Count > 0;

    public string? SelectedColorKey => SelectedColorOption?.Key;

    public WinoIconGlyph SelectedIconGlyph => AccountGroupIcons.GetGlyph(SelectedIconOption?.Icon ?? AccountGroupIcon.PeopleLink);

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        // Other entry points still open the page with the linked inbox they list.
        var context = parameters switch
        {
            AccountGroupDetailsNavigationContext navigationContext => navigationContext,
            MergedAccountProviderDetailViewModel { MergedInbox: { } mergedInbox } when mergedInbox.Id != Guid.Empty
                => new AccountGroupDetailsNavigationContext(mergedInbox.Id),
            MergedAccountProviderDetailViewModel { MergedInbox: { } newMergedInbox }
                => new AccountGroupDetailsNavigationContext(null, newMergedInbox.Name),
            _ => null
        };

        await InitializeAsync(context);
    }

    public async Task InitializeAsync(AccountGroupDetailsNavigationContext? context)
    {
        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var groups = await _accountService.GetAccountGroupsAsync().ConfigureAwait(false);
            var group = context?.GroupId is Guid groupId ? groups.FirstOrDefault(item => item.Id == groupId) : null;
            var groupsById = groups.ToDictionary(item => item.Id);

            await ExecuteUIThread(() =>
            {
                _group = group;
                IsNewGroup = group == null;
                GroupName = group?.Name ?? context?.Name ?? string.Empty;
                CombineFolders = group?.CombineFolders ?? false;
                SelectedColorOption = ColorOptions.FirstOrDefault(option => string.Equals(option.Key, AccountGroupColors.Normalize(group?.ColorKey), StringComparison.Ordinal))
                                      ?? ColorOptions[0];
                SelectedIconOption = IconOptions.FirstOrDefault(option => option.Icon == AccountGroupIcons.Normalize(group?.Icon ?? AccountGroupIcon.PeopleLink))
                                     ?? IconOptions[0];

                MemberEntries.Clear();
                AvailableEntries.Clear();

                foreach (var account in AccountLayoutBuilder.Flatten(AccountLayoutBuilder.Build(accounts, groups)))
                {
                    var currentGroup = account.MergedInboxId is Guid accountGroupId ? groupsById.GetValueOrDefault(accountGroupId) : null;
                    var entry = new AccountGroupEntryViewModel(account, currentGroup);

                    entry.SetEditedGroup(_group?.Id);

                    if (_group != null && currentGroup?.Id == _group.Id)
                        MemberEntries.Add(entry);
                    else
                        AvailableEntries.Add(entry);
                }

                OnPropertyChanged(nameof(HasAvailableEntries));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not load the account group page.");
        }
    }

    partial void OnSelectedColorOptionChanged(AccountGroupColorOption? value)
    {
        foreach (var option in IconOptions)
        {
            option.ColorKey = value?.Key;
        }
    }

    [RelayCommand]
    private void AddMember(AccountGroupEntryViewModel entry)
    {
        if (entry == null || !AvailableEntries.Remove(entry))
            return;

        MemberEntries.Add(entry);
        OnPropertyChanged(nameof(HasAvailableEntries));
    }

    [RelayCommand]
    private void RemoveMember(AccountGroupEntryViewModel entry)
    {
        if (entry == null || !MemberEntries.Remove(entry))
            return;

        AvailableEntries.Add(entry);
        OnPropertyChanged(nameof(HasAvailableEntries));
    }

    private bool CanSave() => !IsBusy && !IsNameMissing && !IsMemberMissing;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;

        try
        {
            var group = _group ?? new MergedInbox { Id = Guid.Empty };

            group.Name = GroupName.Trim();
            group.ColorKey = SelectedColorOption?.Key;
            group.Icon = SelectedIconOption?.Icon ?? AccountGroupIcon.PeopleLink;
            group.CombineFolders = CombineFolders;

            _group = await _accountService.SaveAccountGroupAsync(group, MemberEntries.Select(entry => entry.Id).ToList());

            Messenger.Send(new BackBreadcrumNavigationRequested());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save the account group.");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Messenger.Send(new BackBreadcrumNavigationRequested());

    private bool CanDeleteGroup() => !IsBusy && !IsNewGroup;

    [RelayCommand(CanExecute = nameof(CanDeleteGroup))]
    private async Task DeleteGroupAsync()
    {
        if (_group == null)
            return;

        var isConfirmed = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.DialogMessage_DeleteAccountGroupMessage, _group.Name),
            Translator.DialogMessage_DeleteAccountGroupTitle,
            Translator.AccountGroupDetailsPage_DeleteGroup);

        if (!isConfirmed)
            return;

        IsBusy = true;

        try
        {
            await _accountService.UnlinkMergedInboxAsync(_group.Id);

            Messenger.Send(new BackBreadcrumNavigationRequested());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not delete the account group.");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void MemberEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsMemberMissing));
        SaveCommand.NotifyCanExecuteChanged();
    }
}
