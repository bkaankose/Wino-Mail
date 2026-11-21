namespace Wino.Core.Domain.Enums;

/// <summary>Which tasks the companion lists.</summary>
public enum CompanionTaskBehavior
{
    /// <summary>The tasks in today's My Day.</summary>
    MyDay,

    /// <summary>Open tasks due today or overdue, whether or not they are in My Day.</summary>
    DueDate
}
