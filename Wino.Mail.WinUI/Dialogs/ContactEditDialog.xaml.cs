using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Dialogs;

public sealed partial class ContactEditDialog : ContentDialog
{
    private AccountContact _contact;
    private IDialogServiceBase? _dialogService;

    public AccountContact Contact => _contact;

    public ContactEditDialog(AccountContact? contact = null, IDialogServiceBase? dialogService = null)
    {
        InitializeComponent();
        
        _contact = contact ?? new AccountContact();
        _dialogService = dialogService;
        
        LoadContactData();
        ValidateInput();
    }

    private void LoadContactData()
    {
        if (_contact != null)
        {
            ContactNameTextBox.Text = _contact.Name ?? string.Empty;
            EmailAddressTextBox.Text = _contact.Address ?? string.Empty;

            // Show info badges
            if (_contact.IsRootContact)
            {
                RootContactInfoBorder.Visibility = Visibility.Visible;
            }

            if (_contact.IsOverridden)
            {
                OverriddenContactInfoBorder.Visibility = Visibility.Visible;
            }
        }
    }

    private void ChoosePhotoClicked(object sender, RoutedEventArgs e)
    {
        // TODO: Implement photo picker
    }

    private void RemovePhotoClicked(object sender, RoutedEventArgs e)
    {
        ContactPhotoPersonPicture.ProfilePicture = null;
        RemovePhotoButton.Visibility = Visibility.Collapsed;
    }

    private void ValidateInput(object? sender = null, TextChangedEventArgs? e = null)
    {
        var hasName = !string.IsNullOrWhiteSpace(ContactNameTextBox.Text);
        var hasEmail = !string.IsNullOrWhiteSpace(EmailAddressTextBox.Text);
        var isValidEmail = hasEmail && IsValidEmail(EmailAddressTextBox.Text);

        IsPrimaryButtonEnabled = hasName && isValidEmail;
    }

    private bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Update contact data
        _contact.Name = ContactNameTextBox.Text?.Trim();
        _contact.Address = EmailAddressTextBox.Text?.Trim();

        // Mark as overridden if this was a user edit
        if (!string.IsNullOrEmpty(_contact.Address))
        {
            _contact.IsOverridden = true;
        }
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Nothing to do, dialog will close
    }
}