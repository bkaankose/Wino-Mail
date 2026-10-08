namespace Wino.Core.Domain.Enums;

/// <summary>
/// Whether an account respects the app-wide quiet hours schedule.
/// </summary>
public enum AccountQuietHoursStance
{
    Follow = 0,
    AlwaysNotify = 1,
    NeverOutsideWorkingHours = 2
}
