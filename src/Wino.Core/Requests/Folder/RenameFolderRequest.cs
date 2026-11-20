using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Messaging.UI;

namespace Wino.Core.Requests.Folder;

public record RenameFolderRequest(MailItemFolder Folder, string CurrentFolderName, string NewFolderName) : FolderRequestBase(Folder, FolderSynchronizerOperation.RenameFolder)
{
    public override void ApplyUIChanges(IUIMessageSender sender)
    {
        Folder.FolderName = NewFolderName;
        sender.Send(new FolderRenamed(Folder));
    }

    public override void RevertUIChanges(IUIMessageSender sender)
    {
        Folder.FolderName = CurrentFolderName;
        sender.Send(new FolderRenamed(Folder));
    }
}
