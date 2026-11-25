using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Per-folder row shown on the Folder Customization page. Wraps the underlying
/// <see cref="MailItemFolder"/> entity and exposes observable flags for binding.
/// </summary>
public partial class FolderCustomizationItemViewModel : ObservableObject
{
    public MailItemFolder Folder { get; }

    [ObservableProperty]
    public partial bool IsHidden { get; set; }

    /// <summary>The list is flat, so a shared folder carries its owner's name.</summary>
    public string FolderName => Folder.QualifiedDisplayName;
    public bool IsSystemFolder => Folder.IsSystemFolder;
    public SpecialFolderType SpecialFolderType => Folder.SpecialFolderType;
    public MailFolderScope Scope => Folder.Scope;
    public bool IsContainer => Folder.IsContainer;
    public bool IsSharedOwnerNode => Folder.IsSharedOwnerNode;

    /// <summary>A level of a shared tree can only be hidden, which hides everything below it.</summary>
    public bool CanPinAndRename => !Folder.IsContainer;

    public FolderCustomizationItemViewModel(MailItemFolder folder)
    {
        Folder = folder;
        IsHidden = folder.IsHidden;
    }

    public void NotifyFolderNameChanged() => OnPropertyChanged(nameof(FolderName));

    // List rows without an explicit automation name are announced by ToString(), which was the type name.
    public override string ToString() => FolderName;
}
