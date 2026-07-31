using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Calendar;

public sealed record CalendarContextMenuItem(
    CalendarContextMenuAction Action,
    bool IsPrimary = false,
    bool IsEnabled = true,
    IReadOnlyList<CalendarContextMenuItem> ChildItems = null)
{
    public IReadOnlyList<CalendarContextMenuItem> Children { get; init; } = ChildItems ?? [];

    public bool HasChildren => Children.Count > 0;
}
