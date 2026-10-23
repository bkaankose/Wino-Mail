using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Models.Contacts;

namespace MailCal.Dialogs;

/// <summary>Asks for the name of a new contact list and the account that will own it.</summary>
public sealed partial class NewContactListDialog : ContentDialog
{
    public List<MailAccount> AvailableAccounts { get; }
    public ContactListCreationResult? Result { get; private set; }

    public NewContactListDialog(IReadOnlyList<MailAccount> availableAccounts, MailAccount? selectedAccount)
    {
        AvailableAccounts = [.. availableAccounts];
        InitializeComponent();

        AccountComboBox.SelectedItem = selectedAccount ?? (availableAccounts.Count > 0 ? availableAccounts[0] : null);
    }

    private void InputChanged(object sender, RoutedEventArgs e)
        => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(ListNameTextBox.Text) && AccountComboBox.SelectedItem is MailAccount;

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (AccountComboBox.SelectedItem is MailAccount account)
            Result = new ContactListCreationResult(ListNameTextBox.Text.Trim(), account);
    }
}
