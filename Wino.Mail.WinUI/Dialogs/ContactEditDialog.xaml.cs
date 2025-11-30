using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Dialogs;

public sealed partial class ContactEditDialog : ContentDialog
{
    private Contact _contact;
    private IDialogServiceBase? _dialogService;

    private ObservableCollection<ContactEmail> _emails = new();
    private ObservableCollection<ContactPhone> _phones = new();
    private ObservableCollection<ContactAddress> _addresses = new();

    public Contact Contact => _contact;

    public ContactEditDialog(Contact? contact = null, IDialogServiceBase? dialogService = null)
    {
        InitializeComponent();

        _contact = contact ?? new Contact
        {
            Id = Guid.NewGuid(),
            Source = ContactSource.Manual,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        _dialogService = dialogService;

        LoadContactData();
        ValidateInput();
    }

    private void LoadContactData()
    {
        if (_contact != null)
        {
            // Basic information
            DisplayNameTextBox.Text = _contact.DisplayName ?? string.Empty;
            GivenNameTextBox.Text = _contact.GivenName ?? string.Empty;
            FamilyNameTextBox.Text = _contact.FamilyName ?? string.Empty;
            NicknameTextBox.Text = _contact.Nickname ?? string.Empty;

            // Work information
            CompanyNameTextBox.Text = _contact.CompanyName ?? string.Empty;
            JobTitleTextBox.Text = _contact.JobTitle ?? string.Empty;
            DepartmentTextBox.Text = _contact.Department ?? string.Empty;

            // Additional information
            WebsiteTextBox.Text = _contact.WebsiteUrl ?? string.Empty;
            NotesTextBox.Text = _contact.Notes ?? string.Empty;

            if (_contact.Birthday.HasValue)
            {
                BirthdayDatePicker.Date = _contact.Birthday.Value;
            }

            // Photo
            if (!string.IsNullOrEmpty(_contact.Base64ContactPicture))
            {
                // TODO: Load Base64 photo into PersonPicture
                RemovePhotoButton.Visibility = Visibility.Visible;
            }

            // Show info badges
            if (_contact.IsRootContact)
            {
                RootContactInfoBorder.Visibility = Visibility.Visible;
            }

            if (_contact.HasLocalModifications)
            {
                LocalModificationsInfoBorder.Visibility = Visibility.Visible;
            }

            // Load related entities - these would typically be loaded from database
            // For now, initialize empty collections if contact is new
            EmailsItemsControl.ItemsSource = _emails;
            PhonesItemsControl.ItemsSource = _phones;
            AddressesItemsControl.ItemsSource = _addresses;
        }
    }

    private async void ChoosePhotoClicked(object sender, RoutedEventArgs e)
    {
        if (_dialogService != null)
        {
            var files = await _dialogService.PickFilesAsync(".png", ".jpg", ".jpeg");
            if (files?.Any() == true)
            {
                var file = files.First();
                var base64Image = Convert.ToBase64String(file.Data);
                _contact.Base64ContactPicture = base64Image;

                // TODO: Display photo in PersonPicture
                RemovePhotoButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void RemovePhotoClicked(object sender, RoutedEventArgs e)
    {
        _contact.Base64ContactPicture = null;
        ContactPhotoPersonPicture.ProfilePicture = null;
        RemovePhotoButton.Visibility = Visibility.Collapsed;
    }

    private void AddEmailClicked(object sender, RoutedEventArgs e)
    {
        var newEmail = new ContactEmail
        {
            Id = Guid.NewGuid(),
            ContactId = _contact.Id,
            Type = "work",
            IsPrimary = _emails.Count == 0,
            Order = _emails.Count
        };
        _emails.Add(newEmail);
    }

    private void RemoveEmailClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is ContactEmail email)
        {
            _emails.Remove(email);
        }
    }

    private void AddPhoneClicked(object sender, RoutedEventArgs e)
    {
        var newPhone = new ContactPhone
        {
            Id = Guid.NewGuid(),
            ContactId = _contact.Id,
            Type = "mobile",
            IsPrimary = _phones.Count == 0,
            Order = _phones.Count
        };
        _phones.Add(newPhone);
    }

    private void RemovePhoneClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is ContactPhone phone)
        {
            _phones.Remove(phone);
        }
    }

    private void AddAddressClicked(object sender, RoutedEventArgs e)
    {
        var newAddress = new ContactAddress
        {
            Id = Guid.NewGuid(),
            ContactId = _contact.Id,
            Type = "home",
            IsPrimary = _addresses.Count == 0,
            Order = _addresses.Count
        };
        _addresses.Add(newAddress);
    }

    private void RemoveAddressClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is ContactAddress address)
        {
            _addresses.Remove(address);
        }
    }

    private void ValidateInput(object? sender = null, TextChangedEventArgs? e = null)
    {
        var hasDisplayName = !string.IsNullOrWhiteSpace(DisplayNameTextBox.Text);
        var hasAtLeastOneEmail = _emails.Any(e => !string.IsNullOrWhiteSpace(e.Address));

        IsPrimaryButtonEnabled = hasDisplayName || hasAtLeastOneEmail;
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Update contact data
        _contact.DisplayName = DisplayNameTextBox.Text?.Trim();
        _contact.GivenName = GivenNameTextBox.Text?.Trim();
        _contact.FamilyName = FamilyNameTextBox.Text?.Trim();
        _contact.Nickname = NicknameTextBox.Text?.Trim();

        _contact.CompanyName = CompanyNameTextBox.Text?.Trim();
        _contact.JobTitle = JobTitleTextBox.Text?.Trim();
        _contact.Department = DepartmentTextBox.Text?.Trim();

        _contact.WebsiteUrl = WebsiteTextBox.Text?.Trim();
        _contact.Notes = NotesTextBox.Text?.Trim();
        _contact.Birthday = BirthdayDatePicker.Date.DateTime;

        _contact.ModifiedDate = DateTime.UtcNow;
        _contact.HasLocalModifications = true;

        // Store collections back to contact (caller needs to save these separately to database)
        // This is a limitation of the dialog - related entities need to be saved by the caller
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Nothing to do, dialog will close
    }

    public ObservableCollection<ContactEmail> GetEmails() => _emails;
    public ObservableCollection<ContactPhone> GetPhones() => _phones;
    public ObservableCollection<ContactAddress> GetAddresses() => _addresses;
}
