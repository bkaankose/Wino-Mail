using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

public class TaskSynchronizationOptions
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public TaskSynchronizationType Type { get; set; }
    public Guid? TaskListId { get; set; }

    /// <summary>
    /// True when the user asked for this synchronization. User-initiated pulls skip the Google Tasks quota window,
    /// which only paces automatic synchronization.
    /// </summary>
    public bool IsUserInitiated { get; set; }
}
