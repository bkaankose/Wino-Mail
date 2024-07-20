
using Wino.Core.Domain.Entities;

#if NET8_0
using Microsoft.UI.Xaml.Controls;
#else
using Microsoft.UI.Xaml.Controls;
#endif
namespace Wino.Dialogs
{
    public sealed partial class AccountEditDialog : ContentDialog
    {
        public MailAccount Account { get; private set; }
        public bool IsSaved { get; set; }

        public AccountEditDialog(MailAccount account)
        {
            InitializeComponent();
            Account = account;
        }

        private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            IsSaved = true;
        }
    }
}
