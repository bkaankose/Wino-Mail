using System;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface ITaskActionRequest : IRequestBase
{
    Guid MailAccountId { get; }
    Guid? TaskListId { get; }
    Guid? TaskId { get; }
    Guid? TaskGroupId { get; }
    TaskSynchronizerOperation Operation { get; }
}
