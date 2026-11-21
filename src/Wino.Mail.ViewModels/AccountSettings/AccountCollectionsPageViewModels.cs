#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>An address book or task list of the account, as the account settings list it.</summary>
public sealed class AccountCollectionItem(string title, string detail, bool isOnThisDevice)
{
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public bool IsOnThisDevice { get; } = isOnThisDevice;
}

/// <summary>People of one account: where it syncs and the address books it has.</summary>
public partial class AccountPeopleSettingsPageViewModel : AccountAppPageViewModelBase
{
    private readonly IContactQueryService _contactQueryService;

    public AccountPeopleSettingsPageViewModel(
        IMessenger messenger,
        IAccountService accountService,
        IMailDialogService dialogService,
        IAccountCapabilityService capabilityService,
        AccountAppChangeCoordinator appChanges,
        IContactQueryService contactQueryService)
        : base(messenger, WinoApplicationMode.Contacts, accountService, dialogService, capabilityService, appChanges)
    {
        _contactQueryService = contactQueryService;
    }

    public ObservableCollection<AccountCollectionItem> AddressBooks { get; } = [];

    public bool HasAddressBooks => AddressBooks.Count > 0;

    public bool IsCategoriesVisible => Account?.IsMailAccessGranted == true;

    protected override void OnAccountApplied(MailAccount account) => OnPropertyChanged(nameof(IsCategoriesVisible));

    protected override async Task LoadContentAsync(MailAccount account)
    {
        var books = account.IsContactAccessEnabled
            ? await _contactQueryService.GetAddressBooksAsync(account.Id)
            : [];

        var items = books
            .OrderBy(book => book.SourceKind == ContactSourceKind.Local)
            .ThenByDescending(book => book.IsDefault)
            .ThenBy(book => book.DisplayName)
            .Select(book => new AccountCollectionItem(
                string.IsNullOrWhiteSpace(book.DisplayName) ? Translator.AccountCapability_People : book.DisplayName,
                book.SourceKind == ContactSourceKind.Local ? Translator.AccountApps_SourceDevice : Translator.AccountApps_CollectionSynced,
                book.SourceKind == ContactSourceKind.Local))
            .ToList();

        await ExecuteUIThread(() =>
        {
            AddressBooks.Clear();
            foreach (var item in items)
                AddressBooks.Add(item);

            OnPropertyChanged(nameof(HasAddressBooks));
        });
    }

    [RelayCommand]
    private void EditCategories()
    {
        if (Account != null)
            Messenger.Send(new BreadcrumbNavigationRequested(Translator.MailCategoryManagementPage_Title, WinoPage.MailCategoryManagementPage, Account.Id));
    }
}

/// <summary>To Do of one account: where it syncs and the task lists it has.</summary>
public partial class AccountToDoSettingsPageViewModel : AccountAppPageViewModelBase
{
    private readonly ITaskQueryService _taskQueryService;

    public AccountToDoSettingsPageViewModel(
        IMessenger messenger,
        IAccountService accountService,
        IMailDialogService dialogService,
        IAccountCapabilityService capabilityService,
        AccountAppChangeCoordinator appChanges,
        ITaskQueryService taskQueryService)
        : base(messenger, WinoApplicationMode.Tasks, accountService, dialogService, capabilityService, appChanges)
    {
        _taskQueryService = taskQueryService;
    }

    public ObservableCollection<AccountCollectionItem> TaskLists { get; } = [];

    public bool HasTaskLists => TaskLists.Count > 0;

    protected override async Task LoadContentAsync(MailAccount account)
    {
        List<AccountTaskList> lists = account.IsTaskAccessEnabled
            ? await _taskQueryService.GetTaskListsAsync(account.Id)
            : [];

        var items = lists
            .Where(list => list.MailAccountId == account.Id)
            .OrderBy(list => list.SourceKind == TaskSourceKind.Local)
            .ThenBy(list => list.SortOrder)
            .Select(list => new AccountCollectionItem(
                string.IsNullOrWhiteSpace(list.Title) ? Translator.AccountCapability_ToDo : list.Title,
                list.SourceKind == TaskSourceKind.Local ? Translator.AccountApps_SourceDevice : Translator.AccountApps_CollectionSynced,
                list.SourceKind == TaskSourceKind.Local))
            .ToList();

        await ExecuteUIThread(() =>
        {
            TaskLists.Clear();
            foreach (var item in items)
                TaskLists.Add(item);

            OnPropertyChanged(nameof(HasTaskLists));
        });
    }
}
