using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests
{
    public record MarkFolderAsReadRequest(MailItemFolder Folder, List<MailCopy> MailsToMarkRead) : FolderRequestBase(Folder, MailSynchronizerOperation.MarkFolderRead), ICustomFolderSynchronizationRequest
    {
        public override void ApplyUIChanges() { }

        public override void RevertUIChanges() { }

        public override bool DelayExecution => false;

        public List<Guid> SynchronizationFolderIds => [Folder.Id];
    }
}
