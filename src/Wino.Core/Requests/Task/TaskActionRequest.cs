using System;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Tasks;

/// <summary>Snapshot-backed task/list/step mutation executed by the selected task synchronizer.</summary>
public sealed record TaskActionRequest : ITaskActionRequest
{
    public TaskActionRequest(
        Guid AccountId,
        TaskSynchronizerOperation Operation,
        AccountTaskList List = null,
        AccountTask Task = null,
        AccountTaskStep Step = null,
        AccountTaskList OriginalList = null,
        AccountTask OriginalTask = null,
        AccountTaskStep OriginalStep = null)
    {
        this.AccountId = AccountId;
        this.Operation = Operation;
        this.List = RequestEntityCloner.TaskList(List);
        this.Task = RequestEntityCloner.Task(Task);
        this.Step = RequestEntityCloner.TaskStep(Step);
        this.OriginalList = RequestEntityCloner.TaskList(OriginalList);
        this.OriginalTask = RequestEntityCloner.Task(OriginalTask);
        this.OriginalStep = RequestEntityCloner.TaskStep(OriginalStep);
    }

    public Guid AccountId { get; }
    public TaskSynchronizerOperation Operation { get; }
    public AccountTaskList List { get; }
    public AccountTask Task { get; }
    public AccountTaskStep Step { get; }
    public AccountTaskList OriginalList { get; }
    public AccountTask OriginalTask { get; }
    public AccountTaskStep OriginalStep { get; }
    public Guid MailAccountId => AccountId;
    public Guid? TaskListId => List?.Id ?? Task?.TaskListId;
    public Guid? TaskId => Task?.Id ?? Step?.TaskId;
    public int ResynchronizationDelay => 0;

    public object GroupingKey() => (MailAccountId, TaskListId, TaskId, Operation);

    public void ApplyUIChanges()
        => Send(List, Task, Step, IsDelete(Operation)
            ? OptimisticEntityChange.Delete
            : OptimisticEntityChange.Upsert, EntityUpdateSource.ClientUpdated);

    public void RevertUIChanges()
    {
        if (IsCreate(Operation))
        {
            Send(List, Task, Step, OptimisticEntityChange.Delete, EntityUpdateSource.ClientReverted);
            return;
        }

        Send(
            OriginalList ?? List,
            OriginalTask ?? Task,
            OriginalStep ?? Step,
            OptimisticEntityChange.Upsert,
            EntityUpdateSource.ClientReverted);
    }

    private void Send(
        AccountTaskList list,
        AccountTask task,
        AccountTaskStep step,
        OptimisticEntityChange change,
        EntityUpdateSource source)
        => WeakReferenceMessenger.Default.Send(new TaskStateChanged(
            Operation,
            RequestEntityCloner.TaskList(list),
            RequestEntityCloner.Task(task),
            RequestEntityCloner.TaskStep(step),
            change,
            source));

    private static bool IsCreate(TaskSynchronizerOperation operation)
        => operation is TaskSynchronizerOperation.CreateList or
            TaskSynchronizerOperation.CreateTask or
            TaskSynchronizerOperation.CreateStep;

    private static bool IsDelete(TaskSynchronizerOperation operation)
        => operation is TaskSynchronizerOperation.DeleteList or
            TaskSynchronizerOperation.DeleteTask or
            TaskSynchronizerOperation.DeleteStep;
}
