#if DEBUG
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Mail.ViewModels.Calendar.Messages;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Debug verbs of the event interactions; <c>n</c> is the index into the visible events ordered by start:
/// <c>items</c>, <c>select n</c>, <c>open n</c>, <c>menu n [show [png]]</c>, <c>menu-activate Label/Sub [png]</c>, <c>menu-close</c>, <c>invoke n Label/Sub</c>, <c>slot-menu dayIndex HH:mm|allday [show]</c>,
/// <c>tile-key n Return|Menu</c>, <c>hover n</c>, <c>copy-key</c>, <c>paste-key</c>, <c>copied</c>, <c>paste when time|day</c>, <c>month-dblclick day</c>, <c>selected</c>.
/// </summary>
internal sealed partial class CalendarPageView
{
    private void RegisterInteractionDebugVerbs(WinoCalendarSurface surface)
    {
        Bindings.Own(CalendarDebugActions.AddVerb("items", _ =>
        {
            var text = new StringBuilder();
            var items = VisibleItems();
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                text.Append(CultureInfo.InvariantCulture, $"{index} '{item.Title}' {item.Id} {item.StartDate:yyyy-MM-dd HH:mm}..{item.EndDate:yyyy-MM-dd HH:mm} showAs={item.CalendarItem.ShowAs} recurring={item.IsRecurringEvent} ")
                    .Append(CultureInfo.InvariantCulture, $"needsResponse={item.NeedsResponse} selected={item.IsSelected} tile={surface.FindTile(item) is not null}").AppendLine();
            }
            return text.ToString();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("select", args => WithItem(args, item =>
        {
            if (surface.FindTile(item) is { } tile) tile.DebugRaise(WinoCalendarTile.DebugGesture.Click);
            else Messenger.Send(new CalendarItemTappedMessage(item));
            return $"selected '{item.Title}'";
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("open", args => WithItem(args, item =>
        {
            if (surface.FindTile(item) is { } tile) tile.DebugRaise(WinoCalendarTile.DebugGesture.DoubleClick);
            else Messenger.Send(new CalendarItemDoubleTappedMessage(item));
            return $"opened '{item.Title}'";
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("menu", args => WithItem(args, item =>
        {
            if (args.Length > 1 && args[1] == "show")
            {
                if (surface.FindTile(item) is not { } tile) return "no tile";
                tile.DebugRaise(WinoCalendarTile.DebugGesture.Context);
                if (args.Length > 2 && _openMenu is { } shown) PopoverDebug.Capture(shown, args[2]);
                return $"menu open={_openMenu is not null}\n{_openMenu?.Describe()}";
            }
            return CalendarContextMenus.Describe(MenuEntries(item));
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("invoke", args => WithItem(args, item =>
        {
            if (args.Length < 2) return "usage: invoke n Label/Sub";
            var path = string.Join(' ', args[1..]).Trim('"', '\'');
            if (CalendarContextMenus.Find(MenuEntries(item), path) is not { Run: { } run, IsEnabled: var enabled }) return $"no entry '{path}'";
            if (!enabled) return $"'{path}' is disabled";
            run();
            return $"invoked '{path}' on '{item.Title}'";
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("slot-menu", args =>
        {
            if (args.Length < 2 || !int.TryParse(args[0], out var day)) return "usage: slot-menu dayIndex HH:mm|allday [show]";
            var visible = ViewModel.CurrentVisibleRange;
            if (visible is null) return "no range";
            var date = visible.StartDate.AddDays(day);
            var allDay = args[1] == "allday";
            var start = allDay || !TimeOnly.TryParse(args[1], CultureInfo.InvariantCulture, out var time) ? date.ToDateTime(TimeOnly.MinValue) : date.ToDateTime(time);
            var anchor = new Gdk.Rectangle { X = 100, Y = 100, Width = 1, Height = 1 };
            SurfaceSlotContextRequested(new CalendarSlotEventArgs(start, null, allDay, surface.Root, anchor));
            return $"slot menu for {start:yyyy-MM-dd HH:mm} allDay={allDay} open={_openMenu is not null}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("menu-activate", args =>
        {
            if (_openMenu is not { } menu) return "no menu";
            var path = string.Join(' ', args.Where(arg => !arg.StartsWith('/'))).Trim('"', '\'');
            var activated = menu.Activate(path) || menu.OpenSubmenu(path);
            if (args.Length > 1 && args[^1].StartsWith('/')) PopoverDebug.Capture(menu.Submenu ?? menu.Root, args[^1]);
            return $"activated={activated} '{path}'";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("open-target", args => WithItem(args, item =>
        {
            Messenger.Send(Wino.Mail.ViewModels.Calendar.CalendarPageActivation.CreateSearchResultMessage(item.CalendarItem, ViewModel.StatePersistanceService.CalendarDisplayType));
            return $"pending target '{item.Title}'";
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("menu-close", _ =>
        {
            CloseMenu();
            return $"open menus={ContextMenu.OpenMenus}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("tile-key", args => WithItem(args, item =>
        {
            if (surface.FindTile(item) is not { } tile) return "no tile";
            var keyval = (uint)(args.Length > 1 && args[1] == "Menu" ? Gdk.Constants.KEY_Menu : Gdk.Constants.KEY_Return);
            return $"handled={tile.DebugKey(keyval)}";
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("hover", args => WithItem(args, item =>
        {
            if (surface.FindTile(item) is not { } tile) return "no tile";
            tile.IsHovered = !(args.Length > 1 && args[1] == "off");
            return $"hovered={tile.IsHovered} tooltip='{tile.Root.GetTooltipText()?.Replace('\n', '|')}'";
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("copy-key", _ => $"handled={surface.DebugKey(Gdk.Constants.KEY_c, Gdk.ModifierType.ControlMask)} ring='{_selected?.Title}'"));
        Bindings.Own(CalendarDebugActions.AddVerb("paste-key", _ => $"canPaste={CanPaste} handled={surface.DebugKey(Gdk.Constants.KEY_v, Gdk.ModifierType.ControlMask)}"));
        Bindings.Own(CalendarDebugActions.AddVerb("copied", _ => $"hasCopied={ViewModel.HasCopiedCalendarItem} canPaste={CanPaste}"));
        Bindings.Own(CalendarDebugActions.AddVerb("paste", args =>
        {
            if (args.Length < 2 || !DateTime.TryParseExact(args[0], ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var target))
                return "usage: paste yyyy-MM-ddTHH:mm|yyyy-MM-dd time|day";
            if (!CanPaste) return "nothing to paste";
            Paste(new CalendarSlotEventArgs(target, null, args[1] != "time", surface.Root, new Gdk.Rectangle { X = 100, Y = 100, Width = 1, Height = 1 }));
            return $"pasted at {target:yyyy-MM-dd HH:mm} hasTime={args[1] == "time"}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("month-dblclick", args =>
        {
            if (args.Length < 1 || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return "usage: month-dblclick yyyy-MM-dd";
            SurfaceMonthDayDoubleClicked(day);
            return $"day view {day:yyyy-MM-dd} quick={_quickEvent is not null}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("selected", _ =>
            $"pending='{_pendingTarget?.Item.Title}' {_pendingTarget?.Item.Id} details='{ViewModel.DisplayDetailsCalendarItemViewModel?.Title}' ring='{_selected?.Title}' selectedFlag={_selected?.IsSelected} {DescribePanes()} menus={ContextMenu.OpenMenus}"));
    }

    private List<CalendarItemViewModel> VisibleItems()
    {
        var range = ViewModel.CurrentVisibleRange;
        if (range is null) return [];
        var start = range.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = range.EndDate.ToDateTime(TimeOnly.MinValue).AddDays(1);
        return ViewModel.CalendarItems.Where(item => item.StartDate < end && item.EndDate > start)
            .OrderBy(item => item.StartDate).ThenBy(item => item.Title, StringComparer.Ordinal).ToList();
    }

    private string WithItem(string[] args, Func<CalendarItemViewModel, string> run)
    {
        var items = VisibleItems();
        if (args.Length == 0 || !int.TryParse(args[0], out var index) || index < 0 || index >= items.Count) return $"usage: <verb> <0..{items.Count - 1}>";
        return run(items[index]);
    }
}
#endif
