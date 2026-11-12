using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Creates or edits an account group: its name, its text color and its ordered accounts and linked accounts.
/// Moving accounts between groups happens only here.
/// </summary>
public partial class AccountGroupDetailsPageViewModel : CoreBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;

    private MailAccountGroup? _group;

    public AccountGroupDetailsPageViewModel(IMailDialogService dialogService, IAccountService accountService)
    {
        _dialogService = dialogService;
        _accountService = accountService;

        MemberEntries.CollectionChanged += MemberEntriesChanged;
    }

    public AccountGroupColorOption[] ColorOptions { get; } =
        [new AccountGroupColorOption(null), .. AccountGroupColors.All.Select(color => new AccountGroupColorOption(color))];

    public ObservableCollection<AccountGroupEntryViewModel> MemberEntries { get; } = [];

    public ObservableCollection<AccountGroupEntryViewModel> AvailableEntries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNameMissing))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string GroupName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AccountGroupColorOption? SelectedColorOption { get; set; }

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

    public bool IsMemberMissing => MemberEntries.Count == 0;

    public bool HasAvailableEntries => AvailableEntries.Count > 0;

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        await InitializeAsync(parameters as AccountGroupDetailsNavigationContext);
    }

    public async Task InitializeAsync(AccountGroupDetailsNavigationContext? context)
    {
        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var groups = await _accountService.GetAccountGroupsAsync().ConfigureAwait(false);
            var group = context?.GroupId is Guid groupId ? groups.FirstOrDefault(item => item.Id == groupId) : null;
            var layout = AccountLayoutBuilder.Build(accounts, groups);

            await ExecuteUIThread(() =>
            {
                _group = group;
                IsNewGroup = group == null;
                GroupName = group?.Name ?? string.Empty;
                SelectedColorOption = ColorOptions.FirstOrDefault(option => string.Equals(option.Key, AccountGroupColors.Normalize(group?.ColorKey), StringComparison.Ordinal))
                                      ?? ColorOptions[0];

                MemberEntries.Clear();
                AvailableEntries.Clear();

                foreach (var topLevelEntry in layout)
                {
                    if (topLevelEntry.Kind != AccountLayoutEntryKind.Group)
                    {
                        AddEntry(new AccountGroupEntryViewModel(topLevelEntry, null));
                        continue;
                    }

                    foreach (var child in topLevelEntry.Children)
                    {
                        AddEntry(new AccountGroupEntryViewModel(child, topLevelEntry.Group));
                    }
                }

                OnPropertyChanged(nameof(HasAvailableEntries));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not load the account group page.");
        }
    }

    private void AddEntry(AccountGroupEntryViewModel entry)
    {
        entry.SetEditedGroup(_group?.Id);

        if (_group != null && entry.CurrentGroup?.Id == _group.Id)
            MemberEntries.Add(entry);
        else
            AvailableEntries.Add(entry);
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

    [RelayCommand]
    private void MoveMemberUp(AccountGroupEntryViewModel entry)
    {
        var index = MemberEntries.IndexOf(entry);

        if (index > 0)
            MemberEntries.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveMemberDown(AccountGroupEntryViewModel entry)
    {
        var index = MemberEntries.IndexOf(entry);

        if (index >= 0 && index < MemberEntries.Count - 1)
            MemberEntries.Move(index, index + 1);
    }

    private bool CanSave() => !IsBusy && !IsNameMissing && !IsMemberMissing;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;

        try
        {
            var name = GroupName.Trim();
            var colorKey = SelectedColorOption?.Key;
            var memberIds = MemberEntries.Select(entry => entry.Id).ToList();

            if (_group == null)
            {
                _group = await _accountService.CreateAccountGroupAsync(name, colorKey, memberIds);
            }
            else
            {
                _group.Name = name;
                _group.ColorKey = colorKey;

                await _accountService.UpdateAccountGroupAsync(_group, memberIds);
            }

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
            await _accountService.DeleteAccountGroupAsync(_group.Id);

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
