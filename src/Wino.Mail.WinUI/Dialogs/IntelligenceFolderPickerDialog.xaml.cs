using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Dialogs;

public sealed partial class IntelligenceFolderPickerDialog : ContentDialog
{
    public ObservableCollection<IntelligenceFolderSelectionItem> Folders { get; } = [];

    public IReadOnlyCollection<string> SelectedRemoteFolderIds
        => Folders.Where(folder => folder.IsSelected).Select(folder => folder.RemoteFolderId).ToArray();

    public IntelligenceFolderPickerDialog(IEnumerable<IntelligenceFolderSelectionItem> folders)
    {
        // The dialog edits copies, so cancelling leaves the page's own selection untouched.
        foreach (var folder in folders)
        {
            Folders.Add(new IntelligenceFolderSelectionItem(
                folder.RemoteFolderId,
                folder.DisplayName,
                folder.IsSelected,
                folder.AvailableMessageCount,
                folder.IndentLevel));
        }

        InitializeComponent();
    }
}
