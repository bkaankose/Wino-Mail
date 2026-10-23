using System;
using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Core.Domain.Models.Requests;

public abstract record RequestBase<TOperation> where TOperation : Enum
{
    public RequestTrace Trace { get; set; }
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
    public RequestTrace Trace { get; set; }
    public abstract void ApplyUIChanges();
    public abstract void RevertUIChanges();

    public virtual int ResynchronizationDelay => 0;

    public virtual object GroupingKey() { return Operation; }
}

public abstract record CalendarRequestBase(CalendarItem Item) : RequestBase<CalendarSynchronizerOperation>, ICalendarActionRequest
{
    public virtual Guid? LocalCalendarItemId => Item?.Id;
}

public abstract record CategoryRequestBase(Guid AccountId) : RequestBase<CategorySynchronizerOperation>, ICategoryActionRequest
{
}

public class BatchCollection<TRequestType> : List<TRequestType>, IUIChangeRequest where TRequestType : IUIChangeRequest
{
    public BatchCollection(IEnumerable<TRequestType> collection) : base(collection)
    {
    }
    public virtual void ApplyUIChanges() => ForEach(x => x.ApplyUIChanges());
    public virtual void RevertUIChanges() => ForEach(x => x.RevertUIChanges());
}
