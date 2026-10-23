using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Dialogs;

public sealed partial class AccountPickerDialog : ContentDialog
{
    public MailAccount? PickedAccount { get; set; }

    public List<MailAccount> AvailableAccounts { get; set; } = [];

    public AccountPickerDialog(List<MailAccount> availableAccounts)
    {
        AvailableAccounts = availableAccounts;

        InitializeComponent();
    }

    private void AccountClicked(object sender, ItemClickEventArgs e)
    {
        PickedAccount = e.ClickedItem as MailAccount;

        Hide();
    }
}
