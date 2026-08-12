using EmailValidation;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Dialogs;

public sealed partial class AccountAddressEntryDialog : ContentDialog
{
    public string? EnteredAddress { get; set; }

    public string ConflictingAddress { get; }

    public AccountAddressEntryDialog(string conflictingAddress)
    {
        ConflictingAddress = conflictingAddress;

        InitializeComponent();
    }

    private void AddressChanged(object sender, TextChangedEventArgs e)
    {
        IsPrimaryButtonEnabled = EmailValidator.Validate(AddressBox.Text.Trim());
    }

    private void UseAddressClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        EnteredAddress = AddressBox.Text.Trim();
    }
}
