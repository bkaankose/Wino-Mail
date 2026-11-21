using System;

namespace Wino.Core.Domain.Models.Navigation;

/// <summary>Opens To Do on one task, for example from a mail or an event linked to it.</summary>
public sealed record ToDoNavigationArgs(Guid TaskId);
