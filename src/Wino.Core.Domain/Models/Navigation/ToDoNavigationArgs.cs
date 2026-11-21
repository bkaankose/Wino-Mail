using System;

namespace Wino.Core.Domain.Models.Navigation;

/// <summary>
/// Opens To Do on one task, for example from a mail or an event linked to it, or straight into
/// creating a list when an item could not become a task because no list exists yet.
/// </summary>
public sealed record ToDoNavigationArgs(Guid? TaskId = null, bool CreateList = false);
