using System.Collections.Generic;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;

namespace Wino.Domain.Models.Requests
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
