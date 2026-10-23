using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain;
using MailCal.Editor;

namespace MailCal.Dialogs;

/// <summary>
/// Inserts or edits a link. For a selected image only the address applies, so the text box is hidden.
/// </summary>
public sealed partial class EditorLinkDialog : ContentDialog
{
    /// <summary>
    /// The link to apply, or null when the dialog was cancelled or left without an address.
    /// </summary>
    public EditorLinkCommandArgs? Result { get; private set; }

    public EditorLinkDialog(string url, string text, bool isImageSelected)
    {
        InitializeComponent();

        Title = isImageSelected ? Translator.Composer_EditImageLink : Translator.Composer_InsertLink;
        UrlTextBox.Text = url;
        LinkTextBox.Text = text;
        LinkTextBox.Visibility = isImageSelected ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(UrlTextBox.Text))
            return;

        Result = new EditorLinkCommandArgs(UrlTextBox.Text.Trim(), LinkTextBox.Text.Trim(), OpenInNewWindowCheckBox.IsChecked == true);
    }
}
