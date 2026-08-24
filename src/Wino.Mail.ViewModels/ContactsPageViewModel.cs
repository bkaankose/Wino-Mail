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
using Wino.Messaging.Client.Contacts;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class ContactsPageViewModel : MailBaseViewModel,
    IRecipient<NewContactRequested>, IRecipient<ContactSynchronizationCompleted>,
    IRecipient<NewAddressListRequested>
{
    private const int ContactPageSize = 50;
    private readonly IContactService _contactService;
    private readonly IAccountService _accountService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly INavigationService _navigationService;
    private readonly IMailDialogService _dialogService;
    private readonly ILaunchProtocolService _launchProtocolService;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private CancellationTokenSource _reloadDebounceCancellationTokenSource;
    private Dictionary<Guid, MailAccount> _accounts = [];
    private int _currentOffset;
    private int _currentQueryVersion;
    private int _explicitRefreshDepth;
    private int _suppressSelectedFilterReloadDepth;
    private bool _isInitialized;

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
    public ObservableCollection<AccountContactViewModel> Contacts { get; } = [];
    public ObservableCollection<AccountContactViewModel> SelectedContacts { get; } = [];

    /// <summary>Alphabetical sections over <see cref="Contacts"/>, appended as pages load.</summary>
    public ObservableCollection<ContactAlphaGroup> ContactGroups { get; } = [];

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
        Contacts.CollectionChanged += ContactsCollectionChanged;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        SelectedContacts.CollectionChanged -= SelectedContactsChanged;
        SelectedContacts.CollectionChanged += SelectedContactsChanged;

        if (mode == NavigationMode.Back && _isInitialized)
            return;

        _accounts = (await _accountService.GetAccountsAsync().ConfigureAwait(false)).ToDictionary(account => account.Id);
        await BuildFiltersAsync().ConfigureAwait(false);
        await ReloadContactsAsync().ConfigureAwait(false);
        _isInitialized = true;
    }

    /// <summary>
    /// Rebuilds the sidebar: All / Favorites, then each account's synchronized address
    /// books, then the local lists. Keeps the current selection where it still exists.
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

                FilterGroups.Clear();
                ContactLists.Clear();

                var primary = new ContactFilterGroup(string.Empty)
                {
                    ContactFilterViewModel.CreateAll(Translator.ContactsPage_AllContacts),
                    ContactFilterViewModel.CreateFavorites(Translator.ContactsPage_Favorites)
                };
                primary[1].Count = favoritesCount;
                FilterGroups.Add(primary);

                var bookGroup = new ContactFilterGroup(Translator.ContactsPage_AddressBooks);
                foreach (var book in books.OrderBy(item => _accounts.TryGetValue(item.MailAccountId, out var account) ? account.Name : string.Empty,
                             StringComparer.OrdinalIgnoreCase).ThenByDescending(item => item.IsDefault))
                {
                    _accounts.TryGetValue(book.MailAccountId, out var account);
                    var filter = ContactFilterViewModel.CreateAddressBook(book, account?.Name ?? book.SourceKind.ToString());
                    bookGroup.Add(filter);
                }

                if (bookGroup.Count > 0)
                    FilterGroups.Add(bookGroup);

                var listGroup = new ContactFilterGroup(Translator.ContactsPage_MyLists);
                foreach (var list in lists)
                {
                    ContactLists.Add(list);
                    var filter = ContactFilterViewModel.CreateList(list);
                    filter.Count = listCounts.TryGetValue(list.Id, out var count) ? count : 0;
                    listGroup.Add(filter);
                }

                FilterGroups.Add(listGroup);

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

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);
        SelectedContacts.CollectionChanged -= SelectedContactsChanged;
        CancelPendingReload();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<NewContactRequested>(this);
        Messenger.Register<NewAddressListRequested>(this);
        Messenger.Register<ContactSynchronizationCompleted>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<NewAddressListRequested>(this);
        Messenger.Unregister<NewContactRequested>(this);
        Messenger.Unregister<ContactSynchronizationCompleted>(this);
    }

    void IRecipient<NewContactRequested>.Receive(NewContactRequested message) => _ = ExecuteUIThreadAsync(AddContactAsync);
    void IRecipient<ContactSynchronizationCompleted>.Receive(ContactSynchronizationCompleted message)
    {
        if (Volatile.Read(ref _explicitRefreshDepth) == 0)
            DebounceReload();
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

            await ReloadContactsAsync().ConfigureAwait(false);
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
        if (confirmed) await DeleteContactsInternalAsync([contact.SourceContact]).ConfigureAwait(false);
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

            await _requestDelegator.ExecuteAsync(requests).ConfigureAwait(false);
            await ReloadContactsAsync().ConfigureAwait(false);
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
        var group = ContactGroups.LastOrDefault();

        if (group is null || !string.Equals(group.Key, key, StringComparison.Ordinal))
        {
            group = ContactGroups.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (group is null)
            {
                group = new ContactAlphaGroup(key);
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

            if (SelectedFilter?.Kind == ContactFilterKind.Favorites)
                await ReloadContactsAsync().ConfigureAwait(false);
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
            await _contactService.CreateContactListAsync(name).ConfigureAwait(false);
            await BuildFiltersAsync().ConfigureAwait(false);
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

        try
        {
            filter.List.Name = name.Trim();
            await _contactService.UpdateContactListAsync(filter.List).ConfigureAwait(false);
            await BuildFiltersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
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
            await _contactService.DeleteContactListAsync(filter.List.Id).ConfigureAwait(false);
            var wasSelected = SelectedFilter?.ListId == filter.List.Id;
            if (wasSelected) await ExecuteUIThread(() => SelectedFilter = null);
            await BuildFiltersAsync().ConfigureAwait(false);
            if (wasSelected) await ReloadContactsAsync().ConfigureAwait(false);
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
        if (ids.Count == 0) return;

        try
        {
            await _contactService.AddContactsToListAsync(list.Id, ids).ConfigureAwait(false);
            await BuildFiltersAsync().ConfigureAwait(false);
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

    [RelayCommand]
    private async Task RemoveFromCurrentListAsync(AccountContactViewModel contact)
    {
        if (contact is null || SelectedFilter?.ListId is not Guid listId) return;

        try
        {
            await _contactService.RemoveContactsFromListAsync(listId, [contact.Id]).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                Contacts.Remove(contact);
                RemoveFromGroups(contact);
                if (SelectedContact == contact) SelectedContact = null;
                TotalContactsCount = Math.Max(0, TotalContactsCount - 1);
            });
            await BuildFiltersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.ContactInfoBar_ErrorTitle, ex.Message, InfoBarMessageType.Error);
        }
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
        if (value is null || Volatile.Read(ref _suppressSelectedFilterReloadDepth) > 0) return;

        SelectedContact = null;
        _ = ReloadContactsAsync();
    }

    [RelayCommand] private async Task ToggleSelection() => await ExecuteUIThread(() => { IsSelectionMode = !IsSelectionMode; if (!IsSelectionMode) SelectedContacts.Clear(); });
    [RelayCommand] private async Task SelectAllContacts() => await ExecuteUIThread(() => { SelectedContacts.Clear(); foreach (var contact in Contacts) SelectedContacts.Add(contact); });
    [RelayCommand] private async Task ClearSelection() => await ExecuteUIThread(SelectedContacts.Clear);

    private async void DebounceReload()
    {
        var source = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _reloadDebounceCancellationTokenSource, source);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            await Task.Delay(400, source.Token).ConfigureAwait(false);
            await ReloadContactsAsync().ConfigureAwait(false);
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

    public void Receive(NewAddressListRequested message) => CreateListCommand.Execute(null);
}
