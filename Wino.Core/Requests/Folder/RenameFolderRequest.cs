using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;
using Wino.Core.Domain.Models.Messaging;

namespace Wino.Core.Requests.Folder;

public record RenameFolderRequest(MailItemFolder Folder, string CurrentFolderName, string NewFolderName) : FolderRequestBase(Folder, FolderSynchronizerOperation.RenameFolder)
{
    public override void ApplyUIChanges()
    {
        Folder.FolderName = NewFolderName;
        UIMessagePublisherProvider.Current.Publish(new FolderRenamed(Folder));
    }

    public override void RevertUIChanges()
    {
        Folder.FolderName = CurrentFolderName;
        UIMessagePublisherProvider.Current.Publish(new FolderRenamed(Folder));
    }
}
