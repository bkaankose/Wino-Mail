using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Requests
{
    public abstract record RequestBase<TOperation> where TOperation : Enum
    {
        public virtual void ApplyUIChanges() { }
        public virtual void RevertUIChanges() { }
        public virtual int ResynchronizationDelay => 0;
        public abstract TOperation Operation { get; }
        public virtual object GroupingKey() { return Operation; }
    }

    public abstract record MailRequestBase(MailCopy Item) : RequestBase<MailSynchronizerOperation>, IMailActionRequest
    {
    }

    public abstract record FolderRequestBase(MailItemFolder Folder, FolderSynchronizerOperation Operation) : IFolderActionRequest
    {
        public abstract void ApplyUIChanges();
        public abstract void RevertUIChanges();

        public virtual int ResynchronizationDelay => 0;

        public virtual object GroupingKey() { return Operation; }
    }

    public class BatchCollection<TRequestType> : List<TRequestType>, IUIChangeRequest where TRequestType : IUIChangeRequest
    {
        public BatchCollection(IEnumerable<TRequestType> collection) : base(collection)
        {
        }
        public void ApplyUIChanges() => ForEach(x => x.ApplyUIChanges());
        public void RevertUIChanges() => ForEach(x => x.RevertUIChanges());
    }
}
