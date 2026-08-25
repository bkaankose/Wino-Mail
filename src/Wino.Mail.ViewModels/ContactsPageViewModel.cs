using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class ContactsPageViewModel : MailBaseViewModel,
    IRecipient<ContactSynchronizationCompleted>,
    IBackNavigationAware,
    IShellMenuOwner,
    IShellMenuProvider
{
    /// <summary>
    /// Returning from the contact editor. The list has already reconciled through
    /// <see cref="OnNavigatedTo"/>; this only brings the contact that was just saved
    /// back into view.
    /// </summary>
    public void OnNavigatedBack(object parameter, NavigationResult result)
    {
        if (result is null || result.Kind != NavigationResultKind.Saved || result.Payload is not Guid contactId)
            return;

        _ = ExecuteUIThreadAsync(() => LoadAndSelectContactAsync(contactId));
    }

    private const int ContactPageSize = 50;
    private readonly IContactService _contactService;
    private readonly IAccountService _accountService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly INavigationService _navigationService;
    private readonly IMailDialogService _dialogService;
    private readonly ILaunchProtocolService _launchProtocolService;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private readonly ContactFilterGroup _primaryFilterGroup;
    private readonly ContactFilterGroup _addressBookFilterGroup;
    private readonly ContactFilterGroup _listFilterGroup;
    private CancellationTokenSource _reloadDebounceCancellationTokenSource;
    private Dictionary<Guid, MailAccount> _accounts = [];
    private int _currentOffset;
    private int _currentQueryVersion;
    private int _explicitRefreshDepth;
    private int _suppressSelectedFilterReloadDepth;
    private bool _isInitialized;
    private bool _isPageActive;

    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(LoadMoreContactsCommand))][NotifyPropertyChangedFor(nameof(IsEmpty))] public partial bool IsLoading { get; set; }
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(LoadMoreContactsCommand))] public partial bool IsLoadingMore { get; set; }
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(LoadMoreContactsCommand))] public partial bool HasMoreContacts { get; set; }
    [ObservableProperty] public partial bool IsSelectionMode { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedContactsCommand))]
    [NotifyCanExecuteChangedFor(nameof(FavoriteSelectedContactsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedToListCommand))]
    public partial int SelectedContactsCount { get; set; }
    [ObservableProperty] public partial int TotalContactsCount { get; set; }
    [ObservableProperty] public partial bool IsRefreshing { get; set; }
    [ObservableProperty] public partial ContactFilterViewModel SelectedFilter { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsDetailVisible))] public partial AccountContactViewModel SelectedContact { get; set; }

    public bool IsEmpty => !IsLoading && Contacts.Count == 0;
    public bool CanLoadMoreContacts => HasMoreContacts && !IsLoading && !IsLoadingMore;
    public bool CanDeleteSelectedContacts => SelectedContactsCount > 0;
    public bool IsDetailVisible => SelectedContact is not null;
    public double? ListScrollOffset { get; set; }
    public ObservableCollection<AccountContactViewModel> Contacts { get; } = [];
    public ObservableCollection<AccountContactViewModel> SelectedContacts { get; } = [];

    /// <summary>Alphabetical sections over <see cref="Contacts"/>.</summary>
    public ObservableCollection<ContactGroup> ContactGroups { get; } = [];

    /// <summary>Sidebar sections: primary filters, per-account address books, then local lists.</summary>
    public ObservableCollection<ContactFilterGroup> FilterGroups { get; } = [];

    /// <summary>Local lists, used by the "Add to list" flyout.</summary>
    public ObservableCollection<ContactList> ContactLists { get; } = [];

    public ContactsPageViewModel(IContactService contactService, IAccountService accountService,
        ISynchronizationManager synchronizationManager, IWinoRequestDelegator requestDelegator,
        INavigationService navigationService, IMailDialogService dialogService,
        ILaunchProtocolService launchProtocolService)
    {
        _contactService = contactService;
        _accountService = accountService;
        _synchronizationManager = synchronizationManager;
        _requestDelegator = requestDelegator;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _launchProtocolService = launchProtocolService;
        _primaryFilterGroup = new ContactFilterGroup(string.Empty);
        _addressBookFilterGroup = new ContactFilterGroup(Translator.ContactsPage_AddressBooks);
        _listFilterGroup = new ContactFilterGroup(Translator.ContactsPage_MyLists);
        Contacts.CollectionChanged += ContactsCollectionChanged;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _isPageActive = true;
        SetMenuInteractionEnabled(true);
        SelectedContacts.CollectionChanged -= SelectedContactsChanged;
        SelectedContacts.CollectionChanged += SelectedContactsChanged;

        if (mode == NavigationMode.Back && _isInitialized)
        {
            await ReconcileContactsAsync().ConfigureAwait(false);
            return;
        }

        _accounts = (await _accountService.GetAccountsAsync().ConfigureAwait(false)).ToDictionary(account => account.Id);
        await BuildFiltersAsync().ConfigureAwait(false);
        await ReloadContactsAsync().ConfigureAwait(false);
        _isInitialized = true;
    }

    /// <summary>
    /// Reconciles the sidebar without replacing its observable groups or unchanged items.
    /// </summary>
    private async Task BuildFiltersAsync()
    {
        var books = await _contactService.GetAddressBooksAsync().ConfigureAwait(false);
        var lists = await _contactService.GetContactListsAsync().ConfigureAwait(false);
        var listCounts = await _contactService.GetContactListCountsAsync().ConfigureAwait(false);
        var favoritesCount = await _contactService.GetFavoriteContactsCountAsync().ConfigureAwait(false);

        Interlocked.Increment(ref _suppressSelectedFilterReloadDepth);
        try
        {
            await ExecuteUIThread(() =>
            {
                var previousKind = SelectedFilter?.Kind;
                var previousBookId = SelectedFilter?.AddressBookId;
                var previousListId = SelectedFilter?.ListId;

                ReconcilePrimaryFilters(favoritesCount);
                ReconcileAddressBookFilters(books);
                ReconcileListFilters(lists, listCounts);
                ReconcileFilterGroups();

                var all = FilterGroups.SelectMany(group => group).ToList();
                SelectedFilter = previousKind switch
                {
                    ContactFilterKind.Favorites => all.FirstOrDefault(item => item.Kind == ContactFilterKind.Favorites),
                    ContactFilterKind.AddressBook => all.FirstOrDefault(item => item.AddressBookId == previousBookId),
                    ContactFilterKind.List => all.FirstOrDefault(item => item.ListId == previousListId),
                    _ => null
                } ?? all.FirstOrDefault();
            });
        }
        finally
        {
            Interlocked.Decrement(ref _suppressSelectedFilterReloadDepth);
        }
    }

    private void ReconcilePrimaryFilters(int favoritesCount)
    {
        var all = _primaryFilterGroup.FirstOrDefault(item => item.Kind == ContactFilterKind.All);
        if (all is null)
        {
            all = ContactFilterViewModel.CreateAll(Translator.ContactsPage_AllContacts);
            AttachFilterCallbacks(all);
            _primaryFilterGroup.Insert(0, all);
        }
        else
        {
            all.Name = Translator.ContactsPage_AllContacts;
            MoveToIndex(_primaryFilterGroup, all, 0);
        }

        var favorites = _primaryFilterGroup.FirstOrDefault(item => item.Kind == ContactFilterKind.Favorites);
        if (favorites is null)
        {
            favorites = ContactFilterViewModel.CreateFavorites(Translator.ContactsPage_Favorites);
            AttachFilterCallbacks(favorites);
            _primaryFilterGroup.Insert(Math.Min(1, _primaryFilterGroup.Count), favorites);
        }
        else
        {
            favorites.Name = Translator.ContactsPage_Favorites;
            MoveToIndex(_primaryFilterGroup, favorites, 1);
        }

        favorites.Count = favoritesCount;
        RemoveFiltersExcept(_primaryFilterGroup, (ContactFilterViewModel[])[all, favorites]);
    }

    private void ReconcileAddressBookFilters(IReadOnlyList<ContactAddressBook> books)
    {
        var orderedBooks = books
            .OrderBy(item => _accounts.TryGetValue(item.MailAccountId, out var account) ? account.Name : string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.IsDefault)
            .ToList();
        var desired = new List<ContactFilterViewModel>(orderedBooks.Count);

        for (var targetIndex = 0; targetIndex < orderedBooks.Count; targetIndex++)
        {
            var book = orderedBooks[targetIndex];
            _accounts.TryGetValue(book.MailAccountId, out var account);
            var filter = _addressBookFilterGroup.FirstOrDefault(item => item.AddressBookId == book.Id);

            if (filter is null || filter.AccountId != book.MailAccountId || !ReferenceEquals(filter.Account, account))
            {
                var replacement = ContactFilterViewModel.CreateAddressBook(book, account);
                AttachFilterCallbacks(replacement);
                if (filter is null)
                    _addressBookFilterGroup.Insert(Math.Min(targetIndex, _addressBookFilterGroup.Count), replacement);
                else
                    _addressBookFilterGroup[_addressBookFilterGroup.IndexOf(filter)] = replacement;
                filter = replacement;
            }
            else
            {
                filter.Name = GetAddressBookName(book, account);
            }

            MoveToIndex(_addressBookFilterGroup, filter, targetIndex);
            desired.Add(filter);
        }

        RemoveFiltersExcept(_addressBookFilterGroup, desired);
    }

    private void ReconcileListFilters(
        IReadOnlyList<ContactList> lists,
        IReadOnlyDictionary<Guid, int> listCounts)
    {
        var desiredFilters = new List<ContactFilterViewModel>(lists.Count);
        var desiredLists = new List<ContactList>(lists.Count);

        for (var targetIndex = 0; targetIndex < lists.Count; targetIndex++)
        {
            var incomingList = lists[targetIndex];
            var filter = _listFilterGroup.FirstOrDefault(item => item.ListId == incomingList.Id);
            var list = filter?.List ?? ContactLists.FirstOrDefault(item => item.Id == incomingList.Id) ?? incomingList;
            CopyContactList(incomingList, list);

            if (filter is null)
            {
                filter = ContactFilterViewModel.CreateList(list);
                AttachFilterCallbacks(filter);
                _listFilterGroup.Insert(Math.Min(targetIndex, _listFilterGroup.Count), filter);
            }
            else
            {
                filter.Name = list.Name;
            }

            filter.Count = listCounts.TryGetValue(list.Id, out var count) ? count : 0;
            MoveToIndex(_listFilterGroup, filter, targetIndex);
            desiredFilters.Add(filter);
            desiredLists.Add(list);
        }

        RemoveFiltersExcept(_listFilterGroup, desiredFilters);
        ReconcileContactLists(desiredLists);
    }

    private void ReconcileFilterGroups()
    {
        var desired = _addressBookFilterGroup.Count > 0
            ? new[] { _primaryFilterGroup, _addressBookFilterGroup, _listFilterGroup }
            : new[] { _primaryFilterGroup, _listFilterGroup };

        for (var targetIndex = 0; targetIndex < desired.Length; targetIndex++)
        {
            var group = desired[targetIndex];
            var currentIndex = FilterGroups.IndexOf(group);
            if (currentIndex < 0)
                FilterGroups.Insert(targetIndex, group);
            else if (currentIndex != targetIndex)
                FilterGroups.Move(currentIndex, targetIndex);
        }

        for (var index = FilterGroups.Count - 1; index >= 0; index--)
            if (!desired.Contains(FilterGroups[index]))
                FilterGroups.RemoveAt(index);

        SyncShellMenuItems();
    }

    private void ReconcileContactLists(IReadOnlyList<ContactList> desired)
    {
        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var list = desired[targetIndex];
            var currentIndex = ContactLists.IndexOf(list);
            if (currentIndex < 0)
                ContactLists.Insert(targetIndex, list);
            else if (currentIndex != targetIndex)
                ContactLists.Move(currentIndex, targetIndex);
        }

        for (var index = ContactLists.Count - 1; index >= 0; index--)
            if (!desired.Contains(ContactLists[index]))
                ContactLists.RemoveAt(index);
    }

    private static void RemoveFiltersExcept(
        ContactFilterGroup group,
        IReadOnlyCollection<ContactFilterViewModel> desired)
    {
        for (var index = group.Count - 1; index >= 0; index--)
            if (!desired.Contains(group[index]))
                group.RemoveAt(index);
    }

    private static void MoveToIndex(
        ContactFilterGroup group,
        ContactFilterViewModel filter,
        int targetIndex)
    {
        var currentIndex = group.IndexOf(filter);
        if (currentIndex != targetIndex)
            group.Move(currentIndex, targetIndex);
    }

    private static string GetAddressBookName(ContactAddressBook book, MailAccount account)
        => string.IsNullOrWhiteSpace(book.DisplayName) ? account?.Name ?? book.SourceKind.ToString() : book.DisplayName;

    private static void CopyContactList(ContactList source, ContactList target)
    {
        if (ReferenceEquals(source, target))
            return;

        target.Name = source.Name;
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.SortOrder = source.SortOrder;
        target.CreatedAtUtc = source.CreatedAtUtc;
        target.ModifiedAtUtc = source.ModifiedAtUtc;
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);
        _isPageActive = false;
        SetMenuInteractionEnabled(false);
        SelectedContacts.CollectionChanged -= SelectedContactsChanged;
        CancelPendingReload();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<ContactSynchronizationCompleted>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<ContactSynchronizationCompleted>(this);
    }

    void IRecipient<ContactSynchronizationCompleted>.Receive(ContactSynchronizationCompleted message)
    {
        if (_isPageActive && Volatile.Read(ref _explicitRefreshDepth) == 0)
            DebounceReconcile();
    }
    private async void SelectedContactsChanged(object sender, NotifyCollectionChangedEventArgs e) => await ExecuteUIThread(() => SelectedContactsCount = SelectedContacts.Count);
    private async void ContactsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => await ExecuteUIThread(() => OnPropertyChanged(nameof(IsEmpty)));

    [RelayCommand]
    private async Task RefreshContactsAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        Interlocked.Increment(ref _explicitRefreshDepth);
        CancelPendingReload();
        try
        {
            var results = new List<ContactSynchronizationResult>();
            foreach (var account in _accounts.Values.Where(account => account.IsContactAccessGranted))
                results.Add(await _synchronizationManager.SynchronizeContactsAsync(new() { AccountId = account.Id, Type = ContactSynchronizationType.Delta }).ConfigureAwait(false));

            await ReconcileContactsAsync().ConfigureAwait(false);
            if (results.Any(result => result.CompletedState != SynchronizationCompletedState.Success))
            {
                await ExecuteUIThread(() => _dialogService.InfoBarMessage(
                    Translator.ContactInfoBar_ErrorTitle,
                    Translator.ContactEditor_RefreshFailed,
                    InfoBarMessageType.Warning));
            }
        }
        finally
        {
            Interlocked.Decrement(ref _explicitRefreshDepth);
            await ExecuteUIThread(() => IsRefreshing = false);
        }
    }

    [RelayCommand]
    private async Task ReloadContactsAsync()
    {
        var queryVersion = ++_currentQueryVersion;
        _currentOffset = 0;
        await ExecuteUIThread(() => { HasMoreContacts = false; SelectedContacts.Clear(); });
        await LoadContactsPageAsync(queryVersion, true).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreContacts))]
    private Task LoadMoreContactsAsync() => LoadContactsPageAsync(_currentQueryVersion, false);

    private async Task LoadContactsPageAsync(int queryVersion, bool reset)
    {
        await _loadSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (queryVersion != _currentQueryVersion)
                return;

            await ExecuteUIThread(() => { if (reset) IsLoading = true; else IsLoadingMore = true; });

            try
            {
                var filter = SelectedFilter?.ToQueryFilter(null)
                    ?? new ContactQueryFilter(ExcludeRootContacts: true);

                var page = await _contactService.GetContactsPageAsync(filter, _currentOffset, ContactPageSize).ConfigureAwait(false);

                if (queryVersion != _currentQueryVersion)
                    return;

                await ExecuteUIThread(() =>
                {
                    if (reset)
                    {
                        Contacts.Clear();
                        ContactGroups.Clear();
                    }

                    foreach (var contact in page.Contacts)
                    {
                        _accounts.TryGetValue(contact.MailAccountId, out var account);
                        var item = new AccountContactViewModel(contact, account?.Name, account is null || !account.IsContactReauthorizationRequired);
                        Contacts.Add(item);
                        AppendToGroup(item);
                    }

                    TotalContactsCount = page.TotalCount;
                    HasMoreContacts = page.HasMore;
                    _currentOffset = Contacts.Count;
                });
            }
            catch (Exception ex)
            {
                _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle,
                    string.Format(Translator.ContactInfoBar_FailedToLoadContacts, ex.Message), InfoBarMessageType.Error);
            }
            finally
            {
                await ExecuteUIThread(() => { if (reset) IsLoading = false; else IsLoadingMore = false; });
            }
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    public async Task<IReadOnlyList<AccountContactViewModel>> SearchContactsAsync(string queryText, int limit)
    {
        var search = queryText?.Trim();
        if (string.IsNullOrWhiteSpace(search))
            return [];

        var filter = SelectedFilter?.ToQueryFilter(search)
            ?? new ContactQueryFilter(SearchQuery: search, ExcludeRootContacts: true);
        var page = await _contactService.GetContactsPageAsync(filter, 0, Math.Max(1, limit)).ConfigureAwait(false);

        return page.Contacts.Select(contact =>
        {
            _accounts.TryGetValue(contact.MailAccountId, out var account);
            return new AccountContactViewModel(contact, account?.Name, account is null || !account.IsContactReauthorizationRequired);
        }).ToList();
    }

    public async Task<AccountContactViewModel> LoadAndSelectContactAsync(Guid contactId)
    {
        var queryVersion = _currentQueryVersion;
        var contact = Contacts.FirstOrDefault(item => item.Id == contactId);

        while (contact is null && HasMoreContacts && queryVersion == _currentQueryVersion)
        {
            await LoadContactsPageAsync(queryVersion, false);
            contact = Contacts.FirstOrDefault(item => item.Id == contactId);
        }

        if (contact is not null && queryVersion == _currentQueryVersion)
        {
            SelectedContact = contact;
        }

        return contact;
    }

    [RelayCommand]
    private Task AddContactAsync()
    {
        _navigationService.Navigate(WinoPage.ContactEditPage, new ContactEditNavigationParameter());
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void EditContact(AccountContactViewModel contact)
    {
        if (contact?.IsEditable == true)
            _navigationService.Navigate(WinoPage.ContactEditPage, new ContactEditNavigationParameter(contact.Id));
    }

    [RelayCommand]
    private async Task DeleteContactAsync(AccountContactViewModel contact)
    {
        if (contact?.SourceContact is null || !contact.IsEditable) return;
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.ContactConfirmDialog_DeleteMessage, contact.SourceContact.DisplayValue),
            Translator.ContactConfirmDialog_DeleteTitle, Translator.ContactConfirmDialog_DeleteButton);
        if (confirmed) await DeleteContactsInternalAsync((AccountContact[])[contact.SourceContact]).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedContacts))]
    private async Task DeleteSelectedContactsAsync()
    {
        var contacts = SelectedContacts.Where(item => item.IsEditable).Select(item => item.SourceContact).DistinctBy(item => item.Id).ToList();
        if (contacts.Count == 0) return;
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.ContactConfirmDialog_DeleteMultipleMessage, contacts.Count),
            Translator.ContactConfirmDialog_DeleteTitle, Translator.ContactConfirmDialog_DeleteButton);
        if (confirmed) await DeleteContactsInternalAsync(contacts).ConfigureAwait(false);
    }

    private async Task DeleteContactsInternalAsync(IEnumerable<AccountContact> contacts)
    {
        try
        {
            var requests = contacts
                .Select(contact => new ContactOperationPreparationRequest(ContactSynchronizerOperation.Delete, contact))
                .ToList();

            if (requests.Count == 0)
                return;

            Interlocked.Increment(ref _explicitRefreshDepth);
            try
            {
                await _requestDelegator.ExecuteAsync(requests).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _explicitRefreshDepth);
            }

            var deletedIds = requests.Select(request => request.Contact.Id).ToHashSet();
            await ExecuteUIThread(() => RemoveContacts(deletedIds));
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle,
                string.Format(Translator.ContactInfoBar_FailedToDeleteContacts, ex.Message), InfoBarMessageType.Error);
        }
    }

    /// <summary>
    /// Appends a contact to its alphabetical section. The query is ordered by SortKey,
    /// so a page never introduces a section that is already closed.
    /// </summary>
    private void AppendToGroup(AccountContactViewModel contact)
    {
        var key = contact.InitialLetter;
        var group = ContactGroups.Count > 0 ? ContactGroups[^1] : null;

        if (group is null || !string.Equals(group.Key, key, StringComparison.Ordinal))
        {
            group = ContactGroups.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (group is null)
            {
                group = new ContactGroup(key);
                ContactGroups.Add(group);
            }
        }

        group.Add(contact);
    }

    private void RemoveFromGroups(AccountContactViewModel contact)
    {
        foreach (var group in ContactGroups.ToList())
        {
            if (!group.Remove(contact))
                continue;

            if (group.Count == 0)
                ContactGroups.Remove(group);
            break;
        }
    }

    private void RemoveContacts(IReadOnlySet<Guid> contactIds)
    {
        if (contactIds.Count == 0)
            return;

        foreach (var contact in Contacts.Where(item => contactIds.Contains(item.Id)).ToList())
        {
            Contacts.Remove(contact);
            RemoveFromGroups(contact);
            SelectedContacts.Remove(contact);
        }

        if (SelectedContact is not null && contactIds.Contains(SelectedContact.Id))
            SelectedContact = null;

        TotalContactsCount = Math.Max(0, TotalContactsCount - contactIds.Count);
        _currentOffset = Contacts.Count;
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(AccountContactViewModel contact)
    {
        if (contact is null) return;

        var target = !contact.IsFavorite;
        try
        {
            await _contactService.SetContactFavoriteAsync(contact.Id, target).ConfigureAwait(false);
            await ExecuteUIThread(() => contact.IsFavorite = target);
            await RefreshFavoritesCountAsync().ConfigureAwait(false);

            // Leaving the Favorites view means the contact no longer belongs in the list.
            if (!target && SelectedFilter?.Kind == ContactFilterKind.Favorites)
                await ExecuteUIThread(() =>
                {
                    Contacts.Remove(contact);
                    RemoveFromGroups(contact);
                    if (SelectedContact == contact) SelectedContact = null;
                    TotalContactsCount = Math.Max(0, TotalContactsCount - 1);
                });
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedContacts))]
    private async Task FavoriteSelectedContactsAsync()
    {
        var contacts = SelectedContacts.DistinctBy(item => item.Id).ToList();
        if (contacts.Count == 0) return;

        // Mixed selections become fully favorited rather than toggling item by item.
        var target = contacts.Any(item => !item.IsFavorite);
        try
        {
            await _contactService.SetContactsFavoriteAsync(contacts.Select(item => item.Id), target).ConfigureAwait(false);
            await ExecuteUIThread(() => { foreach (var item in contacts) item.IsFavorite = target; });
            await RefreshFavoritesCountAsync().ConfigureAwait(false);

            if (!target && SelectedFilter?.Kind == ContactFilterKind.Favorites)
            {
                var removedIds = contacts.Select(item => item.Id).ToHashSet();
                await ExecuteUIThread(() => RemoveContacts(removedIds));
            }
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    private async Task RefreshFavoritesCountAsync()
    {
        var count = await _contactService.GetFavoriteContactsCountAsync().ConfigureAwait(false);
        await ExecuteUIThread(() =>
        {
            var favorites = FilterGroups.SelectMany(group => group).FirstOrDefault(item => item.Kind == ContactFilterKind.Favorites);
            if (favorites is not null) favorites.Count = count;
        });
    }

    [RelayCommand]
    private async Task CreateListAsync()
    {
        var name = await _dialogService.ShowTextInputDialogAsync(
            string.Empty, Translator.ContactList_NewTitle, Translator.ContactList_NameHeader, Translator.Buttons_Save);

        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var list = await _contactService.CreateContactListAsync(name.Trim()).ConfigureAwait(false);
            await ExecuteUIThread(() => AddContactList(list));
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task RenameListAsync(ContactFilterViewModel filter)
    {
        if (filter?.List is null) return;

        var name = await _dialogService.ShowTextInputDialogAsync(
            filter.List.Name, Translator.ContactList_RenameTitle, Translator.ContactList_NameHeader, Translator.Buttons_Save);

        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), filter.List.Name, StringComparison.Ordinal)) return;

        var previousName = filter.List.Name;
        try
        {
            var updatedName = name.Trim();
            filter.List.Name = updatedName;
            await _contactService.UpdateContactListAsync(filter.List).ConfigureAwait(false);
            await ExecuteUIThread(() => filter.Name = updatedName);
        }
        catch (Exception ex)
        {
            filter.List.Name = previousName;
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteListAsync(ContactFilterViewModel filter)
    {
        if (filter?.List is null) return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.ContactConfirmDialog_DeleteListMessage, filter.List.Name),
            Translator.ContactConfirmDialog_DeleteListTitle,
            Translator.ContactConfirmDialog_DeleteButton);

        if (!confirmed) return;

        try
        {
            var wasSelected = SelectedFilter?.ListId == filter.List.Id;
            await _contactService.DeleteContactListAsync(filter.List.Id).ConfigureAwait(false);
            await ExecuteUIThread(() => RemoveContactList(filter, wasSelected));

            if (wasSelected)
                await ReconcileContactsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedContacts))]
    private async Task AddSelectedToListAsync(ContactList list)
    {
        if (list is null) return;

        var ids = SelectedContacts.Select(item => item.Id).Distinct().ToList();
        await AssignContactsToListAsync(list, ids).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContactList>> GetAssignableListsAsync(AccountContactViewModel contact)
    {
        if (contact is null)
            return [];

        var availableLists = ContactLists.ToList();

        try
        {
            var assignedListIds = await _contactService.GetListIdsForContactAsync(contact.Id).ConfigureAwait(false);
            var assignedListIdSet = assignedListIds.ToHashSet();

            return availableLists
                .Where(list => !assignedListIdSet.Contains(list.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
            return [];
        }
    }

    public async Task AssignContactsToListAsync(ContactList list, IEnumerable<Guid> contactIds)
    {
        if (list is null)
            return;

        var ids = contactIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        if (ids.Count == 0)
            return;

        try
        {
            await _contactService.AddContactsToListAsync(list.Id, ids).ConfigureAwait(false);
            await RefreshListCountsAsync().ConfigureAwait(false);
            _dialogService.InfoBarMessage(
                Translator.ContactList_AddedTitle,
                string.Format(Translator.ContactList_AddedMessage, ids.Count, list.Name),
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    public IReadOnlyList<Guid> ResolveContactDragIds(IEnumerable<AccountContactViewModel> draggedContacts)
    {
        var dragged = draggedContacts?
            .Where(contact => contact is not null)
            .DistinctBy(contact => contact.Id)
            .ToList() ?? [];
        var selected = SelectedContacts.DistinctBy(contact => contact.Id).ToList();

        if (selected.Count > 1)
        {
            var selectedIds = selected.Select(contact => contact.Id).ToHashSet();
            if (dragged.Any(contact => selectedIds.Contains(contact.Id)))
                return selected.Select(contact => contact.Id).ToList();
        }

        return dragged.Select(contact => contact.Id).ToList();
    }

    [RelayCommand]
    private async Task RemoveFromCurrentListAsync(AccountContactViewModel contact)
    {
        if (contact is null || SelectedFilter?.ListId is not Guid listId) return;

        try
        {
            await _contactService.RemoveContactsFromListAsync(listId, (Guid[])[contact.Id]).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                Contacts.Remove(contact);
                RemoveFromGroups(contact);
                if (SelectedContact == contact) SelectedContact = null;
                TotalContactsCount = Math.Max(0, TotalContactsCount - 1);
            });
            await RefreshListCountsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    private void AddContactList(ContactList list)
    {
        if (ContactLists.All(item => item.Id != list.Id))
            ContactLists.Add(list);

        if (_listFilterGroup.All(item => item.ListId != list.Id))
        {
            var filter = ContactFilterViewModel.CreateList(list);
            _listFilterGroup.Add(filter);
        }

        if (!FilterGroups.Contains(_listFilterGroup))
            FilterGroups.Add(_listFilterGroup);
    }

    private void RemoveContactList(ContactFilterViewModel filter, bool wasSelected)
    {
        if (wasSelected)
        {
            Interlocked.Increment(ref _suppressSelectedFilterReloadDepth);
            try
            {
                SelectedFilter = _primaryFilterGroup.FirstOrDefault(item => item.Kind == ContactFilterKind.All);
            }
            finally
            {
                Interlocked.Decrement(ref _suppressSelectedFilterReloadDepth);
            }
        }

        _listFilterGroup.Remove(filter);
        var list = ContactLists.FirstOrDefault(item => item.Id == filter.ListId);
        if (list is not null)
            ContactLists.Remove(list);
    }

    private async Task RefreshListCountsAsync()
    {
        var listCounts = await _contactService.GetContactListCountsAsync().ConfigureAwait(false);
        await ExecuteUIThread(() =>
        {
            foreach (var filter in _listFilterGroup)
                filter.Count = filter.ListId is Guid listId && listCounts.TryGetValue(listId, out var count) ? count : 0;
        });
    }

    private static bool CanComposeToContact(AccountContactViewModel contact)
        => !string.IsNullOrWhiteSpace(contact?.SourceContact?.PrimaryEmailAddress);

    [RelayCommand(CanExecute = nameof(CanComposeToContact))]
    private void ComposeToContact(AccountContactViewModel contact)
    {
        var address = contact?.SourceContact?.PrimaryEmailAddress;
        if (string.IsNullOrWhiteSpace(address)) return;

        // Reuse the mailto activation path: the shell picks the account and creates the draft.
        _launchProtocolService.MailToUri = new MailToUri($"mailto:{Uri.EscapeDataString(address)}");
        Messenger.Send(new MailtoProtocolMessageRequested());
    }

    [RelayCommand] private void ClearSelectedContact() => SelectedContact = null;

    partial void OnSelectedFilterChanged(ContactFilterViewModel value)
    {
        // The navigation pane binds its selection through IShellMenuProvider.SelectedMenuItem.
        OnPropertyChanged(nameof(IShellMenuProvider.SelectedMenuItem));

        if (value is null || Volatile.Read(ref _suppressSelectedFilterReloadDepth) > 0) return;

        SelectedContact = null;
        _ = ReloadContactsAsync();
    }

    [RelayCommand] private async Task ToggleSelection() => await ExecuteUIThread(() => { IsSelectionMode = !IsSelectionMode; if (!IsSelectionMode) SelectedContacts.Clear(); });
    [RelayCommand] private async Task SelectAllContacts() => await ExecuteUIThread(() => { SelectedContacts.Clear(); foreach (var contact in Contacts) SelectedContacts.Add(contact); });
    [RelayCommand] private async Task ClearSelection() => await ExecuteUIThread(SelectedContacts.Clear);

    private async Task ReconcileContactsAsync()
    {
        var queryVersion = _currentQueryVersion;
        await _loadSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (queryVersion != _currentQueryVersion)
                return;

            var filter = SelectedFilter?.ToQueryFilter(null)
                ?? new ContactQueryFilter(ExcludeRootContacts: true);
            var pageSize = Math.Max(ContactPageSize, Contacts.Count);
            var page = await _contactService.GetContactsPageAsync(filter, 0, pageSize).ConfigureAwait(false);

            if (queryVersion != _currentQueryVersion)
                return;

            var updatedContacts = page.Contacts.Select(CreateContactViewModel).ToList();
            await ExecuteUIThread(() => ReconcileContacts(updatedContacts, page));
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_ErrorTitle,
                string.Format(Translator.ContactInfoBar_FailedToLoadContacts, ex.Message),
                InfoBarMessageType.Error);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    private AccountContactViewModel CreateContactViewModel(AccountContact contact)
    {
        _accounts.TryGetValue(contact.MailAccountId, out var account);
        return new AccountContactViewModel(
            contact,
            account?.Name,
            account is null || !account.IsContactReauthorizationRequired);
    }

    private void ReconcileContacts(
        IReadOnlyList<AccountContactViewModel> updatedContacts,
        PagedContactsResult page)
    {
        var selectedIds = SelectedContacts.Select(item => item.Id).ToHashSet();
        var selectedContactId = SelectedContact?.Id;

        for (var targetIndex = 0; targetIndex < updatedContacts.Count; targetIndex++)
        {
            var updated = updatedContacts[targetIndex];
            var existingIndex = Contacts.IndexOf(Contacts.FirstOrDefault(item => item.Id == updated.Id));

            if (existingIndex < 0)
            {
                Contacts.Insert(targetIndex, updated);
                continue;
            }

            if (existingIndex != targetIndex)
                Contacts.Move(existingIndex, targetIndex);

            if (RequiresReplacement(Contacts[targetIndex], updated))
                Contacts[targetIndex] = updated;
        }

        while (Contacts.Count > updatedContacts.Count)
            Contacts.RemoveAt(Contacts.Count - 1);

        ReconcileContactGroups();

        SelectedContacts.Clear();
        foreach (var contact in Contacts.Where(item => selectedIds.Contains(item.Id)))
            SelectedContacts.Add(contact);

        SelectedContact = selectedContactId is Guid id
            ? Contacts.FirstOrDefault(item => item.Id == id)
            : null;
        TotalContactsCount = page.TotalCount;
        HasMoreContacts = page.HasMore;
        _currentOffset = Contacts.Count;
    }

    private void ReconcileContactGroups()
    {
        var desiredGroups = Contacts
            .GroupBy(contact => contact.InitialLetter)
            .Select(group => (group.Key, Contacts: group.ToList()))
            .ToList();

        for (var targetGroupIndex = 0; targetGroupIndex < desiredGroups.Count; targetGroupIndex++)
        {
            var desired = desiredGroups[targetGroupIndex];
            var group = ContactGroups.FirstOrDefault(item => string.Equals(item.Key, desired.Key, StringComparison.Ordinal));

            if (group is null)
            {
                group = new ContactGroup(desired.Key);
                ContactGroups.Insert(targetGroupIndex, group);
            }
            else
            {
                var currentGroupIndex = ContactGroups.IndexOf(group);
                if (currentGroupIndex != targetGroupIndex)
                    ContactGroups.Move(currentGroupIndex, targetGroupIndex);
            }

            ReconcileGroup(group, desired.Contacts);
        }

        while (ContactGroups.Count > desiredGroups.Count)
            ContactGroups.RemoveAt(ContactGroups.Count - 1);
    }

    private static void ReconcileGroup(
        ContactGroup group,
        IReadOnlyList<AccountContactViewModel> desiredContacts)
    {
        for (var targetIndex = 0; targetIndex < desiredContacts.Count; targetIndex++)
        {
            var desired = desiredContacts[targetIndex];
            var currentIndex = group.IndexOf(group.FirstOrDefault(item => item.Id == desired.Id));

            if (currentIndex < 0)
            {
                group.Insert(targetIndex, desired);
                continue;
            }

            if (currentIndex != targetIndex)
                group.Move(currentIndex, targetIndex);

            if (!ReferenceEquals(group[targetIndex], desired))
                group[targetIndex] = desired;
        }

        while (group.Count > desiredContacts.Count)
            group.RemoveAt(group.Count - 1);
    }

    private static bool RequiresReplacement(
        AccountContactViewModel current,
        AccountContactViewModel updated)
    {
        var currentContact = current.SourceContact;
        var updatedContact = updated.SourceContact;

        return currentContact.ModifiedAtUtc != updatedContact.ModifiedAtUtc ||
               currentContact.IsFavorite != updatedContact.IsFavorite ||
               currentContact.ContactPictureFileId != updatedContact.ContactPictureFileId ||
               currentContact.PendingMutation != updatedContact.PendingMutation ||
               !string.Equals(currentContact.RemoteVersion, updatedContact.RemoteVersion, StringComparison.Ordinal);
    }

    private async void DebounceReconcile()
    {
        var source = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _reloadDebounceCancellationTokenSource, source);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            await Task.Delay(400, source.Token).ConfigureAwait(false);
            if (_isPageActive)
                await ReconcileContactsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelPendingReload()
    {
        var pendingReload = Interlocked.Exchange(ref _reloadDebounceCancellationTokenSource, null);
        pendingReload?.Cancel();
        pendingReload?.Dispose();
    }

}
