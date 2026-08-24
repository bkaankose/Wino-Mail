using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// Triggers a synchronization pass over every enabled calendar.
/// </summary>
public sealed class CalendarSyncMenuItem(ICalendarShellClient client)
    : MenuItemBase<ICalendarShellClient>(client, null)
{
}
