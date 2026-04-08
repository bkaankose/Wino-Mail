using System.Linq;
using FluentAssertions;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class CalendarContextMenuItemServiceTests
{
    private readonly CalendarContextMenuItemService _service = new();

    [Fact]
    public void GetContextMenuItems_ForEditableSingleEvent_ReturnsOpenShowAsAndDeleteAsPrimary()
    {
        var calendarItem = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Editable single event",
            ShowAs = CalendarItemShowAs.Busy
        };

        var items = _service.GetContextMenuItems(calendarItem);

        items.Should().HaveCount(3);
        items.Should().ContainSingle(item => item.Action.ActionType == CalendarContextMenuActionType.Open && item.IsPrimary);
        items.Should().ContainSingle(item => item.Action.ActionType == CalendarContextMenuActionType.ShowAs && item.IsPrimary);
        items.Should().ContainSingle(item => item.Action.ActionType == CalendarContextMenuActionType.Delete && item.IsPrimary);
        items.Should().NotContain(item => item.Action.ActionType == CalendarContextMenuActionType.Respond);

        var showAsItem = items.Single(item => item.Action.ActionType == CalendarContextMenuActionType.ShowAs);
        showAsItem.Children.Should().HaveCount(5);
        showAsItem.Children.Select(child => child.Action.ShowAs).Should().BeEquivalentTo(
            [
                CalendarItemShowAs.Free,
                CalendarItemShowAs.Tentative,
                CalendarItemShowAs.Busy,
                CalendarItemShowAs.OutOfOffice,
                CalendarItemShowAs.WorkingElsewhere
            ]);
    }

    [Fact]
    public void GetContextMenuItems_ForLockedRecurringChild_ReturnsRespondDeleteViewSeriesAndJoinOnline()
    {
        var calendarItem = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Recurring invite",
            IsLocked = true,
            RecurringCalendarItemId = Guid.NewGuid(),
            HtmlLink = "https://contoso.example/meeting"
        };

        var items = _service.GetContextMenuItems(calendarItem);

        items.Should().ContainSingle(item => item.Action.ActionType == CalendarContextMenuActionType.Open && item.IsPrimary);
        items.Should().ContainSingle(item => item.Action.ActionType == CalendarContextMenuActionType.Respond && item.IsPrimary);
        items.Should().ContainSingle(item => item.Action.ActionType == CalendarContextMenuActionType.Delete && item.IsPrimary);
        items.Should().ContainSingle(item => item.Action.ActionType == CalendarContextMenuActionType.Open && item.Action.TargetType == CalendarEventTargetType.Series && !item.IsPrimary);
        items.Should().ContainSingle(item => item.Action.ActionType == CalendarContextMenuActionType.JoinOnline && !item.IsPrimary);

        var respondItem = items.Single(item => item.Action.ActionType == CalendarContextMenuActionType.Respond);
        respondItem.Children.Should().HaveCount(2);
        respondItem.Children.Select(child => child.Action.TargetType).Should().BeEquivalentTo([CalendarEventTargetType.Single, CalendarEventTargetType.Series]);
        respondItem.Children.Should().OnlyContain(child => child.Children.Count == 3);

        var deleteItem = items.Single(item => item.Action.ActionType == CalendarContextMenuActionType.Delete);
        deleteItem.Children.Select(child => child.Action.TargetType).Should().BeEquivalentTo([CalendarEventTargetType.Single, CalendarEventTargetType.Series]);
    }
}
