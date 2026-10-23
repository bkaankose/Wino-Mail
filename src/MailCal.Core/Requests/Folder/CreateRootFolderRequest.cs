using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Requests;

namespace MailCal.Core.Requests.Folder;

public record CreateRootFolderRequest(MailItemFolder Folder, string NewFolderName) : FolderRequestBase(Folder, FolderSynchronizerOperation.CreateRootFolder)
{
    public override void ApplyUIChanges() { }
    public override void RevertUIChanges() { }
}
