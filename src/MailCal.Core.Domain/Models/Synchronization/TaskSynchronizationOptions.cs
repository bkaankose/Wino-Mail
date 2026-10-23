using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Synchronization;

public class TaskSynchronizationOptions
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public TaskSynchronizationType Type { get; set; }
    public Guid? TaskListId { get; set; }
}
