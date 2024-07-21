using CommunityToolkit.Mvvm.Messaging;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Models.Requests;
using Wino.Messaging.Server;

namespace Wino.Services.Requests
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
