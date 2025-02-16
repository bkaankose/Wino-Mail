using System.Collections.Generic;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Dialogs;

public sealed partial class AccountPickerDialog : ContentDialog
{
    public MailAccount PickedAccount { get; set; }

    public List<MailAccount> AvailableAccounts { get; set; }

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
