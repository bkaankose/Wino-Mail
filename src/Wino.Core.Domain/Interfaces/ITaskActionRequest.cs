using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface ITaskActionRequest : IRequestBase
{
    Guid MailAccountId { get; }
    Guid? TaskListId { get; }
    Guid? TaskId { get; }
    TaskSynchronizerOperation Operation { get; }
}
