using System.Collections.Generic;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Requests
{
    public abstract record RequestBase<TBatchRequestType>(MailCopy Item, MailSynchronizerOperation Operation) : IRequest
        where TBatchRequestType : IBatchChangeRequest
    {
        public abstract IBatchChangeRequest CreateBatch(IEnumerable<IRequest> requests);
        public abstract void ApplyUIChanges();
        public abstract void RevertUIChanges();

        public virtual bool DelayExecution => false;
    }

    public abstract record FolderRequestBase(MailItemFolder Folder, MailSynchronizerOperation Operation) : IFolderRequest
    {
        public abstract void ApplyUIChanges();
        public abstract void RevertUIChanges();

        public virtual bool DelayExecution => false;
    }

    public abstract record BatchRequestBase(IEnumerable<IRequest> Items, MailSynchronizerOperation Operation) : IBatchChangeRequest
    {
        public abstract void ApplyUIChanges();
        public abstract void RevertUIChanges();

        public virtual bool DelayExecution => false;
    }
}
