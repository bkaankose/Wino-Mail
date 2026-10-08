namespace Wino.Core.Domain.Enums;

/// <summary>
/// When a task reminder is raised relative to its due time.
/// </summary>
public enum TaskReminderTiming
{
    AtDueTime = 0,
    FifteenMinutesBefore = 1,
    OneHourBefore = 2
}
