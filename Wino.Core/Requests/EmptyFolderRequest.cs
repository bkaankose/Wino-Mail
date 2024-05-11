using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests
{
    public record EmptyFolderRequest(MailItemFolder Folder) : FolderRequestBase(Folder, MailSynchronizerOperation.EmptyFolder)
    {
        public override void ApplyUIChanges() { }

        public override void RevertUIChanges() { }
    }
}
