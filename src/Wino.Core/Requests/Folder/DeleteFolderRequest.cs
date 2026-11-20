using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Messaging.UI;

namespace Wino.Core.Requests.Folder;

public record DeleteFolderRequest(MailItemFolder Folder) : FolderRequestBase(Folder, FolderSynchronizerOperation.DeleteFolder)
{
    public override void ApplyUIChanges(IUIMessageSender sender)
    {
        sender.Send(new FolderDeleted(Folder));
    }

    public override void RevertUIChanges(IUIMessageSender sender) { }
}
