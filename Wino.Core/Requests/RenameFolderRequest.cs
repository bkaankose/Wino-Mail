using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests
{
    public record RenameFolderRequest(MailItemFolder Folder, string CurrentFolderName, string NewFolderName) : FolderRequestBase(Folder, MailSynchronizerOperation.RenameFolder)
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
