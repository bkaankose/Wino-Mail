using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Dialogs;

public sealed partial class MoveMailDialog : ContentDialog
{
    public IMailItemFolder SelectedFolder
    {
        get { return (IMailItemFolder)GetValue(SelectedFolderProperty); }
        set { SetValue(SelectedFolderProperty, value); }
    }

    public static readonly DependencyProperty SelectedFolderProperty = DependencyProperty.Register(nameof(SelectedFolder), typeof(IMailItemFolder), typeof(MoveMailDialog), new PropertyMetadata(null, OnSelectedFolderChanged));


    public List<IMailItemFolder> FolderList { get; set; }

    public MoveMailDialog(List<IMailItemFolder> allFolders)
    {
        InitializeComponent();

        if (allFolders == null) return;

        FolderList = allFolders;
    }

    private static void OnSelectedFolderChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is MoveMailDialog dialog)
        {
            dialog.VerifySelection();
        }
    }

    private void VerifySelection()
    {
        if (SelectedFolder != null)
        {
            // Don't select non-move capable folders like Categories or More.

            if (!SelectedFolder.IsMoveTarget)
            {
                // Warn users for only proper mail folders. Not ghost folders.
                InvalidFolderText.Visibility = Visibility.Visible;
                InvalidFolderText.Text = string.Format(Translator.MoveMailDialog_InvalidFolderMessage, SelectedFolder.FolderName);

                if (FolderTreeView.SelectedItem != null)
                {
                    // Toggle the expansion for the selected container if available.
                    // I don't like the expand arrow touch area. It's better this way.

                    if (FolderTreeView.ContainerFromItem(FolderTreeView.SelectedItem) is Microsoft.UI.Xaml.Controls.TreeViewItem container)
                    {
                        container.IsExpanded = !container.IsExpanded;
                    }
                }
                SelectedFolder = null;
            }
            else
            {
                Hide();
            }
        }
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Hide();
    }
}
