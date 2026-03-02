using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;

namespace Wino.Dialogs;

public sealed partial class ContactEditDialog : ContentDialog
{
    private AccountContact _contact;
    private IDialogServiceBase? _dialogService;
    private IContactPictureFileService? _contactPictureFileService;
    private bool _isEditMode;

    public AccountContact Contact => _contact;

    public ContactEditDialog(AccountContact? contact = null, IDialogServiceBase? dialogService = null)
    {
        InitializeComponent();

        _contact = contact ?? new AccountContact();
        _dialogService = dialogService;
        _contactPictureFileService = App.Current.Services.GetService<IContactPictureFileService>();
        _isEditMode = contact != null && !string.IsNullOrEmpty(contact.Address);

        Title = _isEditMode ? Translator.ContactEditDialog_Title : Translator.ContactEditDialog_AddTitle;

        LoadContactData();
        ValidateInput();
    }

    private void LoadContactData()
    {
        if (_contact != null)
        {
            ContactNameTextBox.Text = _contact.Name ?? string.Empty;
            EmailAddressTextBox.Text = _contact.Address ?? string.Empty;

            // Disable email editing for existing contacts (Address is PK).
            EmailAddressTextBox.IsEnabled = !_isEditMode;

            if (_contact.IsRootContact)
                RootContactInfoBorder.Visibility = Visibility.Visible;

            if (_contact.IsOverridden)
                OverriddenContactInfoBorder.Visibility = Visibility.Visible;

            // Load existing photo — prefer file-based picture, fall back to legacy base64.
            if (_contact.ContactPictureFileId.HasValue && _contactPictureFileService != null)
            {
                var filePath = _contactPictureFileService.GetContactPicturePath(_contact.ContactPictureFileId.Value);
                if (!string.IsNullOrEmpty(filePath))
                {
                    LoadContactPhotoFromFile(filePath);
                    RemovePhotoButton.Visibility = Visibility.Visible;
                }
            }
            else if (!string.IsNullOrEmpty(_contact.Base64ContactPicture))
            {
                LoadContactPhotoFromBase64(_contact.Base64ContactPicture);
                RemovePhotoButton.Visibility = Visibility.Visible;
            }
            else
            {
                ContactPhotoPersonPicture.DisplayName = _contact.Name ?? string.Empty;
                RemovePhotoButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void ChoosePhotoClicked(object sender, RoutedEventArgs e)
    {
        if (_dialogService == null) return;

        try
        {
            var files = await _dialogService.PickFilesAsync(".png", ".jpg", ".jpeg");

            if (files?.Count > 0)
            {
                var file = files[0];

                if (_contactPictureFileService != null)
                {
                    // Delete existing file if replacing.
                    if (_contact.ContactPictureFileId.HasValue)
                        await _contactPictureFileService.DeleteContactPictureAsync(_contact.ContactPictureFileId.Value);

                    var fileId = await _contactPictureFileService.SaveContactPictureAsync(file.Data);
                    _contact.ContactPictureFileId = fileId;

                    var filePath = _contactPictureFileService.GetContactPicturePath(fileId);
                    if (!string.IsNullOrEmpty(filePath))
                        LoadContactPhotoFromFile(filePath);
                }
                else
                {
                    // Fallback to legacy base64 when service is unavailable (e.g. design-time).
                    _contact.Base64ContactPicture = Convert.ToBase64String(file.Data);
                    LoadContactPhotoFromBase64(_contact.Base64ContactPicture);
                }

                RemovePhotoButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // Failed to pick photo, ignore.
        }
    }

    private void RemovePhotoClicked(object sender, RoutedEventArgs e)
    {
        if (_contact.ContactPictureFileId.HasValue && _contactPictureFileService != null)
            _ = _contactPictureFileService.DeleteContactPictureAsync(_contact.ContactPictureFileId.Value);

        _contact.ContactPictureFileId = null;
        _contact.Base64ContactPicture = null;
        ContactPhotoPersonPicture.ProfilePicture = null;
        ContactPhotoPersonPicture.DisplayName = ContactNameTextBox.Text;
        RemovePhotoButton.Visibility = Visibility.Collapsed;
    }

    private void LoadContactPhotoFromFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.SetSource(stream.AsRandomAccessStream());
            ContactPhotoPersonPicture.ProfilePicture = bitmap;
        }
        catch
        {
            // Failed to load image, ignore.
        }
    }

    private void LoadContactPhotoFromBase64(string base64String)
    {
        try
        {
            var imageBytes = Convert.FromBase64String(base64String);
            using var stream = new MemoryStream(imageBytes);
            var bitmap = new BitmapImage();
            bitmap.SetSource(stream.AsRandomAccessStream());
            ContactPhotoPersonPicture.ProfilePicture = bitmap;
        }
        catch
        {
            // Failed to load image, ignore.
        }
    }

    private void ValidateInput(object? sender = null, TextChangedEventArgs? e = null)
    {
        var hasName = !string.IsNullOrWhiteSpace(ContactNameTextBox.Text);
        var hasEmail = !string.IsNullOrWhiteSpace(EmailAddressTextBox.Text);

        var isValidEmail = hasEmail && EmailValidation.EmailValidator.Validate(EmailAddressTextBox.Text);

        if (_isEditMode)
        {
            // In edit mode, only name is required (email is locked).
            IsPrimaryButtonEnabled = hasName;
        }
        else
        {
            // In create mode, both name and valid email are required.
            IsPrimaryButtonEnabled = hasName && isValidEmail;
        }
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _contact.Name = ContactNameTextBox.Text?.Trim();

        if (!_isEditMode)
            _contact.Address = EmailAddressTextBox.Text?.Trim();

        // Mark as overridden if this was a user edit of an existing contact.
        if (_isEditMode)
            _contact.IsOverridden = true;
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Nothing to do, dialog will close.
    }
}
