using Microsoft.UI.Xaml.Controls;
using Wino.Editor;

namespace Wino.Mail.WinUI.Dialogs;

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
        // An empty description leaves the picture undescribed; only the checks before sending mark one decorative.
        Result = new EditorImagePropertiesCommandArgs(
            string.IsNullOrWhiteSpace(AltTextBox.Text) ? null : AltTextBox.Text.Trim(),
            string.IsNullOrWhiteSpace(UrlTextBox.Text) ? null : UrlTextBox.Text.Trim(),
            OpenInNewWindowCheckBox.IsChecked == true);
    }
}
