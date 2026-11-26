using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.WinUI.Dialogs;

public sealed partial class PictureDescriptionDialog : ContentDialog
{
    /// <summary>The saved description, empty when the user saved none; null when the dialog was cancelled.</summary>
    public string? Result { get; private set; }

    public PictureDescriptionDialog(string? currentDescription)
    {
        InitializeComponent();

        DescriptionTextBox.Text = currentDescription ?? string.Empty;
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        => Result = DescriptionTextBox.Text.Trim();
}
