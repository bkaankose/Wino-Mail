using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Folder
{
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
}
