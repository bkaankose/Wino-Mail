using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface ITaskQueryService
{
    Task<List<AccountTaskListGroup>> GetTaskListGroupsAsync(Guid? accountId = null);
    Task<List<AccountTaskList>> GetTaskListsAsync(Guid? accountId = null);
    Task<AccountTaskList> GetTaskListAsync(Guid listId);
    Task<List<AccountTask>> GetTasksAsync(Guid? accountId = null, Guid? listId = null, TaskViewKind view = TaskViewKind.All, string search = null, TaskSortKind sort = TaskSortKind.DueDate);
    Task<List<AccountTask>> GetMyDaySuggestionsAsync();
    Task<AccountTask> GetTaskAsync(Guid taskId);
}
