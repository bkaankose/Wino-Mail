using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Tasks;

public record TaskActionRequest(
    Guid AccountId,
    TaskSynchronizerOperation Operation,
    AccountTaskList List = null,
    AccountTask Task = null,
    AccountTaskStep Step = null) : ITaskActionRequest
{
    public Guid MailAccountId => AccountId;
    public Guid? TaskListId => List?.Id ?? Task?.TaskListId;
    public Guid? TaskId => Task?.Id ?? Step?.TaskId;
    public int ResynchronizationDelay => 0;
    public object GroupingKey() => (MailAccountId, TaskListId, TaskId, Operation);
    public void ApplyUIChanges() { }
    public void RevertUIChanges() { }
}
