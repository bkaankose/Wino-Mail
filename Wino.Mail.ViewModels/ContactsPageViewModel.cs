using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels;

public partial class ContactsPageViewModel : MailBaseViewModel
{
    private readonly IContactService _contactService;
    private readonly IMailDialogService _dialogService;

    private List<AccountContact> _allContacts = new();

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = false;

    [ObservableProperty]
    public partial bool IsSelectionMode { get; set; } = false;

    [ObservableProperty]
    public partial int SelectedContactsCount { get; set; } = 0;

    public ObservableCollection<AccountContact> Contacts { get; } = new();
    public ObservableCollection<AccountContact> SelectedContacts { get; } = new();

    public ContactsPageViewModel(IContactService contactService, IMailDialogService dialogService)
    {
        _contactService = contactService;
        _dialogService = dialogService;


    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        SelectedContacts.CollectionChanged -= SelectedContactsChanged;
        SelectedContacts.CollectionChanged += SelectedContactsChanged;

        await LoadContactsAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        SelectedContacts.CollectionChanged -= SelectedContactsChanged;
    }

    private void SelectedContactsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => SelectedContactsCount = SelectedContacts.Count;

    [RelayCommand]
    private async Task LoadContactsAsync()
    {
        IsLoading = true;

        try
        {
            _allContacts = await _contactService.GetAllContactsAsync();
            await FilterContactsAsync();
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage("Error", $"Failed to load contacts: {ex.Message}", InfoBarMessageType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SearchContactsAsync()
    {
        await FilterContactsAsync();
    }

    private async Task FilterContactsAsync()
    {
        List<AccountContact> filteredContacts;

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            filteredContacts = _allContacts;
        }
        else
        {
            filteredContacts = await _contactService.SearchContactsAsync(SearchQuery);
        }

        await ExecuteUIThread(() =>
        {
            Contacts.Clear();
            foreach (var contact in filteredContacts.OrderBy(c => c.Name ?? c.Address))
            {
                Contacts.Add(contact);
            }
        });
    }

    [RelayCommand]
    private async Task AddContactAsync()
    {
        var result = await _dialogService.ShowEditContactDialogAsync(null);

        if (result != null)
        {
            try
            {
                var newContact = await _contactService.CreateNewContactAsync(result.Address, result.Name);

                if (!string.IsNullOrEmpty(result.Base64ContactPicture))
                {
                    newContact.Base64ContactPicture = result.Base64ContactPicture;
                    await _contactService.UpdateContactAsync(newContact);
                }

                _allContacts.Add(newContact);
                await FilterContactsAsync();

                _dialogService.InfoBarMessage("Success", "Contact added successfully", InfoBarMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.InfoBarMessage("Error", $"Failed to add contact: {ex.Message}", InfoBarMessageType.Error);
            }
        }
    }

    [RelayCommand]
    private async Task EditContactAsync(AccountContact contact)
    {
        if (contact == null) return;

        var result = await _dialogService.ShowEditContactDialogAsync(contact);

        if (result != null)
        {
            try
            {
                // Update the contact properties
                contact.Name = result.Name;
                contact.Base64ContactPicture = result.Base64ContactPicture;
                contact.IsOverridden = result.IsOverridden;

                await _contactService.UpdateContactAsync(contact);

                // Update the UI
                var index = _allContacts.FindIndex(c => c.Address == contact.Address);
                if (index >= 0)
                {
                    _allContacts[index] = contact;
                }

                await FilterContactsAsync();

                _dialogService.InfoBarMessage("Success", "Contact updated successfully", InfoBarMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.InfoBarMessage("Error", $"Failed to update contact: {ex.Message}", InfoBarMessageType.Error);
            }
        }
    }

    [RelayCommand]
    private async Task DeleteContactAsync(AccountContact contact)
    {
        if (contact == null || contact.IsRootContact)
        {
            _dialogService.InfoBarMessage("Cannot Delete", "Root contacts cannot be deleted", InfoBarMessageType.Warning);
            return;
        }

        var result = await _dialogService.ShowConfirmationDialogAsync(
            $"Are you sure you want to delete the contact '{contact.Name ?? contact.Address}'?",
            "Delete Contact",
            "Delete");

        if (result)
        {
            await DeleteContactsInternalAsync(new[] { contact });
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedContactsAsync()
    {
        if (SelectedContacts.Count == 0) return;

        var deletableContacts = SelectedContacts.Where(c => !c.IsRootContact).ToList();

        if (deletableContacts.Count == 0)
        {
            _dialogService.InfoBarMessage("Cannot Delete", "Root contacts cannot be deleted", InfoBarMessageType.Warning);
            return;
        }

        var result = await _dialogService.ShowConfirmationDialogAsync(
            $"Are you sure you want to delete {deletableContacts.Count} contact(s)?",
            "Delete Contacts",
            "Delete");

        if (result)
        {
            await DeleteContactsInternalAsync(deletableContacts);
        }
    }

    private async Task DeleteContactsInternalAsync(IEnumerable<AccountContact> contactsToDelete)
    {
        try
        {
            var addresses = contactsToDelete.Select(c => c.Address);
            await _contactService.DeleteContactsAsync(addresses);

            // Update local collections
            foreach (var contact in contactsToDelete.ToList())
            {
                _allContacts.Remove(contact);
                SelectedContacts.Remove(contact);
            }

            await FilterContactsAsync();

            _dialogService.InfoBarMessage("Success", "Contacts deleted successfully", InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage("Error", $"Failed to delete contacts: {ex.Message}", InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private void ToggleSelection()
    {
        IsSelectionMode = !IsSelectionMode;

        if (!IsSelectionMode)
        {
            SelectedContacts.Clear();
        }
    }

    [RelayCommand]
    private void SelectAllContacts()
    {
        SelectedContacts.Clear();
        foreach (var contact in Contacts)
        {
            SelectedContacts.Add(contact);
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectedContacts.Clear();
    }

    [RelayCommand]
    private async Task PickContactPhotoAsync(AccountContact contact)
    {
        if (contact == null) return;

        try
        {
            var files = await _dialogService.PickFilesAsync(".png", ".jpg", ".jpeg");

            if (files?.Any() == true)
            {
                var file = files.First();
                var base64Image = Convert.ToBase64String(file.Data);

                contact.Base64ContactPicture = base64Image;
                await _contactService.UpdateContactAsync(contact);

                await FilterContactsAsync();
                _dialogService.InfoBarMessage("Success", "Contact photo updated successfully", InfoBarMessageType.Success);
            }
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage("Error", $"Failed to update photo: {ex.Message}", InfoBarMessageType.Error);
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        // Debounce search - implement if needed
        SearchContactsCommand.ExecuteAsync(null);
    }
}
