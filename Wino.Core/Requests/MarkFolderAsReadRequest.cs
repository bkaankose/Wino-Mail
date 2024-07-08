using System.Collections.Generic;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests
{
    public record MarkFolderAsReadRequest(MailItemFolder Folder, List<MailCopy> MailsToDelete) : FolderRequestBase(Folder, MailSynchronizerOperation.MarkFolderRead)
    {
        public override void ApplyUIChanges() { }

        public override void RevertUIChanges() { }
    }
}
