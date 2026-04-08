using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Services;

public class CalendarContextMenuItemService : ICalendarContextMenuItemService
{
    private static readonly IReadOnlyList<CalendarItemShowAs> ShowAsOptions =
    [
        CalendarItemShowAs.Free,
        CalendarItemShowAs.Tentative,
        CalendarItemShowAs.Busy,
        CalendarItemShowAs.OutOfOffice,
        CalendarItemShowAs.WorkingElsewhere
    ];

    private static readonly IReadOnlyList<CalendarItemStatus> ResponseOptions =
    [
        CalendarItemStatus.Accepted,
        CalendarItemStatus.Tentative,
        CalendarItemStatus.Cancelled
    ];

    public IReadOnlyList<CalendarContextMenuItem> GetContextMenuItems(CalendarItem calendarItem)
    {
        if (calendarItem == null)
            return [];

        var items = new List<CalendarContextMenuItem>
        {
            new(new CalendarContextMenuAction(CalendarContextMenuActionType.Open, CalendarEventTargetType.Single), IsPrimary: true)
        };

        if (calendarItem.IsLocked)
        {
            items.Add(CreateRespondItem(calendarItem.IsRecurringChild));
        }
        else
        {
            items.Add(CreateShowAsItem(calendarItem.IsRecurringChild));
        }

        items.Add(CreateDeleteItem(calendarItem.IsRecurringChild));

        if (calendarItem.IsRecurringChild)
        {
            items.Add(new CalendarContextMenuItem(
                new CalendarContextMenuAction(CalendarContextMenuActionType.Open, CalendarEventTargetType.Series)));
        }

        if (!string.IsNullOrWhiteSpace(calendarItem.HtmlLink))
        {
            items.Add(new CalendarContextMenuItem(
                new CalendarContextMenuAction(CalendarContextMenuActionType.JoinOnline)));
        }

        return items;
    }

    private static CalendarContextMenuItem CreateDeleteItem(bool isRecurringChild)
        => isRecurringChild
            ? new CalendarContextMenuItem(
                new CalendarContextMenuAction(CalendarContextMenuActionType.Delete),
                IsPrimary: true,
                ChildItems:
                [
                    CreateScopeLeaf(CalendarContextMenuActionType.Delete, CalendarEventTargetType.Single),
                    CreateScopeLeaf(CalendarContextMenuActionType.Delete, CalendarEventTargetType.Series)
                ])
            : new CalendarContextMenuItem(
                new CalendarContextMenuAction(CalendarContextMenuActionType.Delete, CalendarEventTargetType.Single),
                IsPrimary: true);

    private static CalendarContextMenuItem CreateShowAsItem(bool isRecurringChild)
        => new(
            new CalendarContextMenuAction(CalendarContextMenuActionType.ShowAs),
            IsPrimary: true,
            ChildItems: isRecurringChild
                ? [CreateScopedShowAsMenu(CalendarEventTargetType.Single), CreateScopedShowAsMenu(CalendarEventTargetType.Series)]
                : CreateShowAsLeaves(CalendarEventTargetType.Single));

    private static CalendarContextMenuItem CreateRespondItem(bool isRecurringChild)
        => new(
            new CalendarContextMenuAction(CalendarContextMenuActionType.Respond),
            IsPrimary: true,
            ChildItems: isRecurringChild
                ? [CreateScopedResponseMenu(CalendarEventTargetType.Single), CreateScopedResponseMenu(CalendarEventTargetType.Series)]
                : CreateResponseLeaves(CalendarEventTargetType.Single));

    private static CalendarContextMenuItem CreateScopedShowAsMenu(CalendarEventTargetType targetType)
        => new(
            new CalendarContextMenuAction(CalendarContextMenuActionType.ShowAs, targetType),
            ChildItems: CreateShowAsLeaves(targetType));

    private static CalendarContextMenuItem CreateScopedResponseMenu(CalendarEventTargetType targetType)
        => new(
            new CalendarContextMenuAction(CalendarContextMenuActionType.Respond, targetType),
            ChildItems: CreateResponseLeaves(targetType));

    private static IReadOnlyList<CalendarContextMenuItem> CreateShowAsLeaves(CalendarEventTargetType targetType)
    {
        var items = new List<CalendarContextMenuItem>(ShowAsOptions.Count);

        foreach (var showAs in ShowAsOptions)
        {
            items.Add(new CalendarContextMenuItem(
                new CalendarContextMenuAction(CalendarContextMenuActionType.ShowAs, targetType, showAs)));
        }

        return items;
    }

    private static IReadOnlyList<CalendarContextMenuItem> CreateResponseLeaves(CalendarEventTargetType targetType)
    {
        var items = new List<CalendarContextMenuItem>(ResponseOptions.Count);

        foreach (var responseStatus in ResponseOptions)
        {
            items.Add(new CalendarContextMenuItem(
                new CalendarContextMenuAction(CalendarContextMenuActionType.Respond, targetType, ResponseStatus: responseStatus)));
        }

        return items;
    }

    private static CalendarContextMenuItem CreateScopeLeaf(CalendarContextMenuActionType actionType, CalendarEventTargetType targetType)
        => new(new CalendarContextMenuAction(actionType, targetType));
}
