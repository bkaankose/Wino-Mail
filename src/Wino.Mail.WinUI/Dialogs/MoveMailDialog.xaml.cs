using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
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


    public List<IMailItemFolder> FolderList { get; set; } = [];

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
        if (SelectedFolder is null)
        {
            return;
        }

        if (SelectedFolder.SpecialFolderType == SpecialFolderType.More)
        {
            if (FolderTreeView.ContainerFromItem(FolderTreeView.SelectedItem) is TreeViewItem container)
            {
                container.IsExpanded = !container.IsExpanded;
            }

            InvalidFolderBorder.Visibility = Visibility.Collapsed;
            SelectedFolder = null!;
            return;
        }

        if (!SelectedFolder.IsMoveTarget)
        {
            InvalidFolderBorder.Visibility = Visibility.Visible;
            InvalidFolderText.Text = string.Format(Translator.MoveMailDialog_InvalidFolderMessage, SelectedFolder.FolderName);
            SelectedFolder = null!;
        }
        else
        {
            Hide();
        }
    }

    private void CancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Hide();
    }
}
