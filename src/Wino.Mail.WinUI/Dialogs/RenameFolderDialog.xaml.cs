using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Dialogs;

/// <summary>Asks for a folder's server name and its local-only display name.</summary>
public sealed partial class RenameFolderDialog : ContentDialog
{
    public FolderRenameDialogResult? Result { get; private set; }

    public RenameFolderDialog(string folderName, string? displayName)
    {
        InitializeComponent();

        FolderNameTextBox.Text = folderName;
        DisplayNameTextBox.Text = displayName ?? string.Empty;
        FolderNameTextBox.SelectAll();
    }

    private void InputChanged(object sender, TextChangedEventArgs e)
        => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(FolderNameTextBox.Text);

    private void RenameClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        => Result = new FolderRenameDialogResult(FolderNameTextBox.Text.Trim(), DisplayNameTextBox.Text.Trim());
}
