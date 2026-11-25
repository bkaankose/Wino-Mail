using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface ITaskQueryService
{
    Task<List<AccountTaskListGroup>> GetTaskListGroupsAsync(Guid? accountId = null);
    Task<List<AccountTaskList>> GetTaskListsAsync(Guid? accountId = null);
    Task<AccountTaskList> GetTaskListAsync(Guid listId);
    Task<List<AccountTask>> GetTasksAsync(Guid? accountId = null, Guid? listId = null, TaskViewKind view = TaskViewKind.All, string search = null, TaskSortKind sort = TaskSortKind.DueDate);
    Task<List<AccountTask>> GetMyDaySuggestionsAsync();
    Task<AccountTask> GetTaskAsync(Guid taskId);

    /// <summary>Which fields the list's task source stores on its server; the rest stay in the local database.</summary>
    Task<TaskCapabilities> GetTaskCapabilitiesAsync(Guid listId);

    /// <summary>Open tasks with a reminder (or a snooze) that falls in the window, for the reminder poller.</summary>
    Task<List<AccountTask>> GetTasksWithRemindersAsync(DateTime fromUtc, DateTime toUtc);
}
