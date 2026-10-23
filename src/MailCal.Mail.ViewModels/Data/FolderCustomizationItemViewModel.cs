using CommunityToolkit.Mvvm.ComponentModel;
using MailCal.Core.Domain.Entities.Mail;

namespace MailCal.Mail.ViewModels.Data;

/// <summary>
/// Per-folder row shown on the Folder Customization page. Wraps the underlying
/// <see cref="MailItemFolder"/> entity and exposes observable flags for binding.
/// </summary>
public partial class FolderCustomizationItemViewModel : ObservableObject
{
    public MailItemFolder Folder { get; }

    [ObservableProperty]
    public partial bool IsHidden { get; set; }

    public string FolderName => Folder.FolderName;
    public bool IsSystemFolder => Folder.IsSystemFolder;
    public Core.Domain.Enums.SpecialFolderType SpecialFolderType => Folder.SpecialFolderType;

    public FolderCustomizationItemViewModel(MailItemFolder folder)
    {
        Folder = folder;
        IsHidden = folder.IsHidden;
    }
}
