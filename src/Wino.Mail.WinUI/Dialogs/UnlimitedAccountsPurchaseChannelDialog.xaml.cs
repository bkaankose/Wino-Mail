using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Dialogs;

public sealed partial class UnlimitedAccountsPurchaseChannelDialog : ContentDialog
{
    public bool IsWinoAccountAvailable { get; }

    public string WinoAccountDescription => IsWinoAccountAvailable
        ? Translator.UnlimitedAccountsPurchaseDialog_WinoAccountDescription
        : Translator.UnlimitedAccountsPurchaseDialog_WinoAccountSignInRequired;

    public UnlimitedAccountsPurchaseChannel? Result { get; private set; }

    public UnlimitedAccountsPurchaseChannelDialog(bool isWinoAccountAvailable)
    {
        IsWinoAccountAvailable = isWinoAccountAvailable;

        InitializeComponent();
    }

    private void PrimaryButtonClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Result = WinoAccountOption.IsChecked == true
            ? UnlimitedAccountsPurchaseChannel.WinoAccount
            : UnlimitedAccountsPurchaseChannel.MicrosoftStore;
    }
}
