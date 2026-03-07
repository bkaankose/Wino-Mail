using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class ContactsPageViewModel : MailBaseViewModel
{
    private const int ContactPageSize = 50;

    private readonly IContactService _contactService;
    private readonly IMailDialogService _dialogService;
    private readonly IContactPictureFileService _contactPictureFileService;

    private CancellationTokenSource _searchDebounceCancellationTokenSource;
    private int _currentOffset = 0;
    private int _currentQueryVersion = 0;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreContactsCommand))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; } = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreContactsCommand))]
    public partial bool IsLoadingMore { get; set; } = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreContactsCommand))]
    public partial bool HasMoreContacts { get; set; } = false;

    [ObservableProperty]
    public partial bool IsSelectionMode { get; set; } = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedContactsCommand))]
    public partial int SelectedContactsCount { get; set; } = 0;

    [ObservableProperty]
    public partial int TotalContactsCount { get; set; } = 0;

    public bool IsEmpty => !IsLoading && Contacts.Count == 0;
    public bool CanLoadMoreContacts => HasMoreContacts && !IsLoading && !IsLoadingMore;
    public bool CanDeleteSelectedContacts => SelectedContactsCount > 0;

    public ObservableCollection<AccountContactViewModel> Contacts { get; } = new();
    public ObservableCollection<AccountContactViewModel> SelectedContacts { get; } = new();

    public ContactsPageViewModel(IContactService contactService, IMailDialogService dialogService, IContactPictureFileService contactPictureFileService)
    {
        _contactService = contactService;
        _dialogService = dialogService;
        _contactPictureFileService = contactPictureFileService;

        Contacts.CollectionChanged += ContactsCollectionChanged;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        SelectedContacts.CollectionChanged -= SelectedContactsChanged;
        SelectedContacts.CollectionChanged += SelectedContactsChanged;

        await ReloadContactsAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        SelectedContacts.CollectionChanged -= SelectedContactsChanged;

        _searchDebounceCancellationTokenSource?.Cancel();
        _searchDebounceCancellationTokenSource?.Dispose();
        _searchDebounceCancellationTokenSource = null;
    }

    private async void SelectedContactsChanged(object sender, NotifyCollectionChangedEventArgs e)
        => await ExecuteUIThread(() => { SelectedContactsCount = SelectedContacts.Count; });

    private async void ContactsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        => await ExecuteUIThread(() => { OnPropertyChanged(nameof(IsEmpty)); });

    [RelayCommand]
    private async Task ReloadContactsAsync()
    {
        var queryVersion = ++_currentQueryVersion;
        _currentOffset = 0;

        await ExecuteUIThread(() =>
        {
            HasMoreContacts = false;
            Contacts.Clear();
            SelectedContacts.Clear();
        });

        await LoadContactsPageAsync(queryVersion, reset: true);
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreContacts))]
    private async Task LoadMoreContactsAsync()
    {
        await LoadContactsPageAsync(_currentQueryVersion, reset: false);
    }

    private async Task LoadContactsPageAsync(int queryVersion, bool reset)
    {
        if (IsLoading || IsLoadingMore)
            return;

        await ExecuteUIThread(() =>
        {
            if (reset)
                IsLoading = true;
            else
                IsLoadingMore = true;
        });

        try
        {
            var searchQuery = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim();
            var page = await _contactService.GetContactsPageAsync(
                _currentOffset,
                ContactPageSize,
                searchQuery,
                excludeRootContacts: true).ConfigureAwait(false);

            if (queryVersion != _currentQueryVersion)
                return;

            await ExecuteUIThread(() =>
            {
                if (reset)
                {
                    Contacts.Clear();
                }

                foreach (var contact in page.Contacts)
                {
                    Contacts.Add(new AccountContactViewModel(contact));
                }

                TotalContactsCount = page.TotalCount;
                HasMoreContacts = page.HasMore;
                _currentOffset = Contacts.Count;
            });
        }
        catch (Exception ex)
        {
            if (queryVersion != _currentQueryVersion)
                return;

            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_ErrorTitle,
                string.Format(Translator.ContactInfoBar_FailedToLoadContacts, ex.Message),
                InfoBarMessageType.Error);
        }
        finally
        {
            if (queryVersion == _currentQueryVersion)
            {
                await ExecuteUIThread(() =>
                {
                    if (reset)
                        IsLoading = false;
                    else
                        IsLoadingMore = false;
                });
            }
        }
    }

    [RelayCommand]
    private async Task AddContactAsync()
    {
        var result = await _dialogService.ShowEditContactDialogAsync(null);

        if (result == null) return;

        try
        {
            var newContact = await _contactService.CreateNewContactAsync(result.Address, result.Name);

            if (result.ContactPictureFileId.HasValue)
            {
                newContact.ContactPictureFileId = result.ContactPictureFileId;
                await _contactService.UpdateContactAsync(newContact);
            }

            await ReloadContactsAsync();

            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_SuccessTitle,
                Translator.ContactInfoBar_ContactAdded,
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_ErrorTitle,
                string.Format(Translator.ContactInfoBar_FailedToAddContact, ex.Message),
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task EditContactAsync(AccountContactViewModel contactViewModel)
    {
        var contact = contactViewModel?.SourceContact;
        if (contact == null) return;

        var result = await _dialogService.ShowEditContactDialogAsync(contact);

        if (result == null) return;

        try
        {
            contact.Name = result.Name;
            contact.ContactPictureFileId = result.ContactPictureFileId;
            contact.IsOverridden = result.IsOverridden;

            await _contactService.UpdateContactAsync(contact);
            await ReloadContactsAsync();

            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_SuccessTitle,
                Translator.ContactInfoBar_ContactUpdated,
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_ErrorTitle,
                string.Format(Translator.ContactInfoBar_FailedToUpdateContact, ex.Message),
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteContactAsync(AccountContactViewModel contactViewModel)
    {
        var contact = contactViewModel?.SourceContact;
        if (contact == null || contact.IsRootContact)
        {
            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_WarningTitle,
                Translator.ContactInfoBar_CannotDeleteRoot,
                InfoBarMessageType.Warning);
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.ContactConfirmDialog_DeleteMessage, contact.Name ?? contact.Address),
            Translator.ContactConfirmDialog_DeleteTitle,
            Translator.ContactConfirmDialog_DeleteButton);

        if (confirmed)
        {
            await DeleteContactsInternalAsync(new[] { contact });
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedContacts))]
    private async Task DeleteSelectedContactsAsync()
    {
        if (SelectedContacts.Count == 0) return;

        var deletableContacts = SelectedContacts
            .Select(c => c?.SourceContact)
            .Where(c => c != null && !c.IsRootContact)
            .GroupBy(c => c.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (deletableContacts.Count == 0)
        {
            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_WarningTitle,
                Translator.ContactInfoBar_CannotDeleteRoot,
                InfoBarMessageType.Warning);
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.ContactConfirmDialog_DeleteMultipleMessage, deletableContacts.Count),
            Translator.ContactConfirmDialog_DeleteTitle,
            Translator.ContactConfirmDialog_DeleteButton);

        if (confirmed)
        {
            await DeleteContactsInternalAsync(deletableContacts);
        }
    }

    private async Task DeleteContactsInternalAsync(IEnumerable<AccountContact> contactsToDelete)
    {
        try
        {
            var addresses = contactsToDelete
                .Select(c => c.Address)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (addresses.Count == 0) return;

            await _contactService.DeleteContactsAsync(addresses);
            await ReloadContactsAsync();

            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_SuccessTitle,
                Translator.ContactInfoBar_ContactsDeleted,
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_ErrorTitle,
                string.Format(Translator.ContactInfoBar_FailedToDeleteContacts, ex.Message),
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task ToggleSelection()
    {
        await ExecuteUIThread(() =>
        {
            IsSelectionMode = !IsSelectionMode;

            if (!IsSelectionMode)
            {
                SelectedContacts.Clear();
            }
        });
    }

    [RelayCommand]
    private async Task SelectAllContacts()
    {
        await ExecuteUIThread(() =>
        {
            SelectedContacts.Clear();

            foreach (var contact in Contacts)
            {
                SelectedContacts.Add(contact);
            }
        });
    }

    [RelayCommand]
    private async Task ClearSelection()
    {
        await ExecuteUIThread(() => { SelectedContacts.Clear(); });
    }

    [RelayCommand]
    private async Task PickContactPhotoAsync(AccountContactViewModel contactViewModel)
    {
        var contact = contactViewModel?.SourceContact;
        if (contact == null) return;

        try
        {
            var files = await _dialogService.PickFilesAsync(".png", ".jpg", ".jpeg");

            if (files?.Any() == true)
            {
                var file = files.First();

                if (contact.ContactPictureFileId.HasValue)
                    await _contactPictureFileService.DeleteContactPictureAsync(contact.ContactPictureFileId.Value);

                contact.ContactPictureFileId = await _contactPictureFileService
                    .SaveContactPictureAsync(file.Data)
                    .ConfigureAwait(false);

                await _contactService.UpdateContactAsync(contact);
                await RefreshContactInUiAsync(contact);

                _dialogService.InfoBarMessage(
                    Translator.ContactInfoBar_SuccessTitle,
                    Translator.ContactInfoBar_ContactPhotoUpdated,
                    InfoBarMessageType.Success);
            }
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.ContactInfoBar_ErrorTitle,
                string.Format(Translator.ContactInfoBar_FailedToUpdatePhoto, ex.Message),
                InfoBarMessageType.Error);
        }
    }

    private async Task RefreshContactInUiAsync(AccountContact contact)
    {
        if (contact == null || string.IsNullOrWhiteSpace(contact.Address))
            return;

        await ExecuteUIThread(() =>
        {
            ReplaceContactByAddress(Contacts, contact);
            ReplaceContactByAddress(SelectedContacts, contact);
        });
    }

    private static void ReplaceContactByAddress(ObservableCollection<AccountContactViewModel> source, AccountContact updatedContact)
    {
        var index = source
            .Select((item, i) => new { item, i })
            .FirstOrDefault(x => string.Equals(x.item.Address, updatedContact.Address, StringComparison.OrdinalIgnoreCase))
            ?.i ?? -1;

        if (index < 0) return;

        source[index] = new AccountContactViewModel(CloneContact(updatedContact));
    }

    private static AccountContact CloneContact(AccountContact contact)
        => new()
        {
            Address = contact.Address,
            Name = contact.Name,
            ContactPictureFileId = contact.ContactPictureFileId,
            IsRootContact = contact.IsRootContact,
            IsOverridden = contact.IsOverridden
        };

    partial void OnSearchQueryChanged(string value)
    {
        DebounceSearchAndReload();
    }

    private async void DebounceSearchAndReload()
    {
        _searchDebounceCancellationTokenSource?.Cancel();
        _searchDebounceCancellationTokenSource?.Dispose();

        _searchDebounceCancellationTokenSource = new CancellationTokenSource();

        try
        {
            await Task.Delay(250, _searchDebounceCancellationTokenSource.Token);
            await ReloadContactsAsync();
        }
        catch (OperationCanceledException)
        {
            // Ignore stale search input.
        }
    }
}
