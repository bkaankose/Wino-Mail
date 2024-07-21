using System.Collections.Generic;
using Wino.Domain.Entities;


#if NET8_0
using Microsoft.UI.Xaml.Controls;
#else
using Microsoft.UI.Xaml.Controls;
#endif

namespace Wino.Dialogs
{
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
}
