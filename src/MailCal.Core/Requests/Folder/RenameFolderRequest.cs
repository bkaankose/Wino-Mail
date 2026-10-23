using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Requests;
using MailCal.Messaging.UI;

namespace MailCal.Core.Requests.Folder;

public record RenameFolderRequest(MailItemFolder Folder, string CurrentFolderName, string NewFolderName) : FolderRequestBase(Folder, FolderSynchronizerOperation.RenameFolder)
{
    public override void ApplyUIChanges()
    {
        Folder.FolderName = NewFolderName;
        WeakReferenceMessenger.Default.Send(new FolderRenamed(Folder));
    }

    public override void RevertUIChanges()
    {
        Folder.FolderName = CurrentFolderName;
        WeakReferenceMessenger.Default.Send(new FolderRenamed(Folder));
    }
}
