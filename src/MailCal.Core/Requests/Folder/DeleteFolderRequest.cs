using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Requests;
using MailCal.Messaging.UI;

namespace MailCal.Core.Requests.Folder;

public record DeleteFolderRequest(MailItemFolder Folder) : FolderRequestBase(Folder, FolderSynchronizerOperation.DeleteFolder)
{
    public override void ApplyUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new FolderDeleted(Folder));
    }

    public override void RevertUIChanges() { }
}
