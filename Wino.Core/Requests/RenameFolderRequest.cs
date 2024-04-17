using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests
{
    public record RenameFolderRequest(MailItemFolder Folder) : FolderRequestBase(Folder, MailSynchronizerOperation.RenameFolder)
    {
        public override void ApplyUIChanges()
        {

        }

        public override void RevertUIChanges()
        {

        }
    }
}
