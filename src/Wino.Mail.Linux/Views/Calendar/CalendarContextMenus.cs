using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Presentation;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>One entry of the event menu, as data: the menu is built from it and the debug verbs read it.</summary>
internal sealed record CalendarMenuEntry(string Title, Action? Run, bool IsEnabled, WinoIconGlyph Glyph, bool IsDestructive, bool IsRadio, bool IsSelected,
    CalendarMenuEntry[] Children);

/// <summary>
/// The event context menu (Windows CalendarItemCommandBarFlyout, macOS CalendarContextMenuLabels): labels, enabled
/// state and glyphs of the shared calendar menu entries; Show-as leaves are radio items with the current value marked.
/// </summary>
internal static class CalendarContextMenus
{
    public static string Label(CalendarContextMenuAction action)
    {
        if (action.ActionType == CalendarContextMenuActionType.ShowAs && action.ShowAs is { } showAs)
            return ShowAsText(showAs);
        if (action.ActionType == CalendarContextMenuActionType.Respond && action.ResponseStatus is { } status)
            return status switch
            {
                CalendarItemStatus.Tentative => Translator.CalendarEventResponse_Tentative,
                CalendarItemStatus.Cancelled => Translator.CalendarEventResponse_Decline,
                _ => Translator.CalendarEventResponse_Accept
            };
        if (action.TargetType.HasValue && action.ActionType is CalendarContextMenuActionType.Delete or CalendarContextMenuActionType.ShowAs or CalendarContextMenuActionType.Respond)
            return action.TargetType == CalendarEventTargetType.Single ? Translator.CalendarContextMenu_ThisEventOnly : Translator.CalendarContextMenu_AllEventsInSeries;
        return action.ActionType switch
        {
            CalendarContextMenuActionType.Open when action.TargetType == CalendarEventTargetType.Series => Translator.CalendarItem_DetailsPopup_ViewSeriesButton,
            CalendarContextMenuActionType.JoinOnline => Translator.CalendarItem_DetailsPopup_JoinOnline,
            CalendarContextMenuActionType.Delete => Translator.Buttons_Delete,
            CalendarContextMenuActionType.ShowAs => Translator.CalendarEventDetails_ShowAs,
            CalendarContextMenuActionType.Respond => Translator.CalendarContextMenu_Respond,
            CalendarContextMenuActionType.Copy => Translator.Buttons_Copy,
            CalendarContextMenuActionType.CreateTask => Translator.CalendarContextMenu_CreateTask,
            _ => Translator.Buttons_Open
        };
    }

    public static bool IsEnabled(CalendarContextMenuItem entry, CalendarItemViewModel item)
        => CalendarEventDisplay.CanRunMenuEntry(entry.IsEnabled, item, item.IsBusy, entry.Action.ActionType);

    public static string ShowAsText(CalendarItemShowAs showAs) => showAs switch
    {
        CalendarItemShowAs.Free => Translator.CalendarShowAs_Free,
        CalendarItemShowAs.Tentative => Translator.CalendarShowAs_Tentative,
        CalendarItemShowAs.OutOfOffice => Translator.CalendarShowAs_OutOfOffice,
        CalendarItemShowAs.WorkingElsewhere => Translator.CalendarShowAs_WorkingElsewhere,
        _ => Translator.CalendarShowAs_Busy
    };

    public static WinoIconGlyph Glyph(CalendarContextMenuAction action) => action.ActionType switch
    {
        CalendarContextMenuActionType.Open => action.TargetType == CalendarEventTargetType.Series ? WinoIconGlyph.EventEditSeries : WinoIconGlyph.OpenInNewWindow,
        CalendarContextMenuActionType.JoinOnline => WinoIconGlyph.EventJoinOnline,
        CalendarContextMenuActionType.Delete => WinoIconGlyph.Delete,
        CalendarContextMenuActionType.ShowAs => WinoIconGlyph.CalendarShowAs,
        CalendarContextMenuActionType.Respond => WinoIconGlyph.EventRespond,
        CalendarContextMenuActionType.Copy => WinoIconGlyph.Copy,
        CalendarContextMenuActionType.CreateTask => WinoIconGlyph.TaskList,
        _ => WinoIconGlyph.None
    };

    /// <summary>The entries for <paramref name="item"/>; <paramref name="run"/> performs a leaf's action.</summary>
    public static CalendarMenuEntry[] Build(IReadOnlyList<CalendarContextMenuItem> entries, CalendarItemViewModel item, Action<CalendarContextMenuAction> run)
    {
        var result = new CalendarMenuEntry[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var action = entry.Action;
            var children = entry.HasChildren ? Build(entry.Children, item, run) : [];
            var isRadio = !entry.HasChildren && action.ActionType == CalendarContextMenuActionType.ShowAs && action.ShowAs is not null;
            result[index] = new CalendarMenuEntry(Label(action), entry.HasChildren ? null : () => run(action), IsEnabled(entry, item), Glyph(action),
                action.ActionType == CalendarContextMenuActionType.Delete, isRadio, isRadio && item.CalendarItem.ShowAs == action.ShowAs, children);
        }
        return result;
    }

    public static void Append(WinoMenuBuilder builder, CalendarMenuEntry[] entries, string group = "showas")
    {
        foreach (var entry in entries)
        {
            if (entry.Children.Length > 0)
            {
                var nested = group + "/" + entry.Title;
                builder.Submenu(entry.Title, sub => Append(sub, entry.Children, nested), entry.IsEnabled, icon: entry.Glyph);
            }
            else if (entry.IsRadio) builder.Radio(group, entry.Title, entry.IsSelected, entry.Run ?? (static () => { }), icon: entry.Glyph, enabled: entry.IsEnabled);
            else builder.Item(entry.Title, entry.Run ?? (static () => { }), entry.IsEnabled, icon: entry.Glyph, destructive: entry.IsDestructive);
        }
    }

    /// <summary>The first entry along a slash-separated title path ("Show as/Tentative").</summary>
    public static CalendarMenuEntry? Find(CalendarMenuEntry[] entries, string path)
    {
        var parts = path.Split('/');
        CalendarMenuEntry? found = null;
        var level = entries;
        foreach (var part in parts)
        {
            found = Array.Find(level, entry => string.Equals(entry.Title, part, StringComparison.OrdinalIgnoreCase));
            if (found is null) return null;
            level = found.Children;
        }
        return found;
    }

    public static string Describe(CalendarMenuEntry[] entries)
    {
        var text = new StringBuilder();
        Describe(text, entries, 0);
        return text.ToString();
    }

    private static void Describe(StringBuilder text, CalendarMenuEntry[] entries, int depth)
    {
        foreach (var entry in entries)
        {
            text.Append(new string('>', depth)).Append(entry.Title).Append(" enabled=").Append(entry.IsEnabled);
            if (entry.IsRadio) text.Append(" radio selected=").Append(entry.IsSelected);
            if (entry.IsDestructive) text.Append(" destructive");
            if (entry.Glyph != WinoIconGlyph.None) text.Append(" glyph=").Append(entry.Glyph);
            text.AppendLine();
            Describe(text, entry.Children, depth + 1);
        }
    }
}
