using Microsoft.UI.Xaml.Controls;
using MailCal.Editor;

namespace MailCal.Dialogs;

public sealed partial class EditorImagePropertiesDialog : ContentDialog
{
    /// <summary>
    /// The properties to apply, or null when the dialog was cancelled.
    /// </summary>
    public EditorImagePropertiesCommandArgs? Result { get; private set; }

    public EditorImagePropertiesDialog(string altText, string linkUrl)
    {
        InitializeComponent();

        AltTextBox.Text = altText;
        UrlTextBox.Text = linkUrl;
    }

    private void ApplyClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Result = new EditorImagePropertiesCommandArgs(
            AltTextBox.Text.Trim(),
            string.IsNullOrWhiteSpace(UrlTextBox.Text) ? null : UrlTextBox.Text.Trim(),
            OpenInNewWindowCheckBox.IsChecked == true);
    }
}
