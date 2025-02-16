using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Dialogs;

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
