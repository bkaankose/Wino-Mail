using Microsoft.UI.Xaml.Controls;

namespace MailCal.Dialogs;

/// <summary>
/// Message, confirmation and multi-choice prompts. The icon, text and optional "don't ask again"
/// check box come from <see cref="CustomMessageDialogInformationContainer"/>; the caller only names the buttons.
/// </summary>
public sealed partial class MailCalMessageDialog : ContentDialog
{
    public CustomMessageDialogInformationContainer Information { get; }

    public MailCalMessageDialog(CustomMessageDialogInformationContainer information,
                             string primaryButtonText,
                             string secondaryButtonText = "",
                             string closeButtonText = "",
                             ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        InitializeComponent();

        Information = information;
        Content = information;
        PrimaryButtonText = primaryButtonText;
        SecondaryButtonText = secondaryButtonText;
        CloseButtonText = closeButtonText;
        DefaultButton = defaultButton;
    }
}
