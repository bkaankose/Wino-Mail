using CommunityToolkit.Mvvm.Messaging;
using UIKit;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Calendar.ViewModels.Messages;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>Labels and glyphs for the shared calendar context menu actions (Windows CalendarItemCommandBarFlyout; Mac twin).</summary>
public static class CalendarContextMenuLabels
{
    public static string Label(CalendarContextMenuAction action)
    {
        if (action.ActionType == CalendarContextMenuActionType.ShowAs && action.ShowAs is { } showAs && action.TargetType is null)
            return ShowAsText(showAs);
        if (action.ActionType == CalendarContextMenuActionType.Respond && action.ResponseStatus is { } status && action.TargetType is null)
            return status switch
            {
                CalendarItemStatus.Accepted => Translator.CalendarEventResponse_Accept,
                CalendarItemStatus.Tentative => Translator.CalendarEventResponse_Tentative,
                CalendarItemStatus.Cancelled => Translator.CalendarEventResponse_Decline,
                _ => Translator.CalendarEventResponse_Accept
            };
        if (action.TargetType.HasValue && action.ActionType is CalendarContextMenuActionType.Delete or CalendarContextMenuActionType.ShowAs or CalendarContextMenuActionType.Respond)
            return action.TargetType == CalendarEventTargetType.Single
                ? Translator.CalendarContextMenu_ThisEventOnly
                : Translator.CalendarContextMenu_AllEventsInSeries;
        return action.ActionType switch
        {
            CalendarContextMenuActionType.Open when action.TargetType == CalendarEventTargetType.Series => Translator.CalendarItem_DetailsPopup_ViewSeriesButton,
            CalendarContextMenuActionType.Open => Translator.Buttons_Open,
            CalendarContextMenuActionType.JoinOnline => Translator.CalendarItem_DetailsPopup_JoinOnline,
            CalendarContextMenuActionType.Delete => Translator.Buttons_Delete,
            CalendarContextMenuActionType.ShowAs => Translator.CalendarEventDetails_ShowAs,
            CalendarContextMenuActionType.Respond => Translator.CalendarContextMenu_Respond,
            _ => Translator.Buttons_Open
        };
    }

    /// <summary>
    /// Windows CalendarItemCommandBarFlyout: busy items take no action, and a read-only calendar's events cannot be
    /// deleted, re-marked (Show as) or answered.
    /// </summary>
    public static bool IsEnabled(CalendarContextMenuItem entry, CalendarItemViewModel item)
    {
        bool isMutation = entry.Action.ActionType is CalendarContextMenuActionType.Delete
            or CalendarContextMenuActionType.ShowAs or CalendarContextMenuActionType.Respond;
        return entry.IsEnabled && !item.IsBusy && (!isMutation || !CalendarTileMapper.IsReadOnly(item));
    }

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
        CalendarContextMenuActionType.Open => action.TargetType == CalendarEventTargetType.Series ? WinoIconGlyph.EventEditSeries : WinoIconGlyph.Calendar,
        CalendarContextMenuActionType.JoinOnline => WinoIconGlyph.EventJoinOnline,
        CalendarContextMenuActionType.Delete => action.TargetType is null ? WinoIconGlyph.Delete : WinoIconGlyph.None,
        CalendarContextMenuActionType.ShowAs => action.TargetType is null && action.ShowAs is null ? WinoIconGlyph.CalendarShowAs : WinoIconGlyph.None,
        CalendarContextMenuActionType.Respond => action.ResponseStatus switch
        {
            CalendarItemStatus.Accepted when action.TargetType is null => WinoIconGlyph.EventAccept,
            CalendarItemStatus.Tentative when action.TargetType is null => WinoIconGlyph.EventTentative,
            CalendarItemStatus.Cancelled when action.TargetType is null => WinoIconGlyph.EventDecline,
            null => WinoIconGlyph.EventRespond,
            _ => WinoIconGlyph.None
        },
        _ => WinoIconGlyph.None
    };

    /// <summary>
    /// The shared context menu of <paramref name="item"/> as a UIMenu (the Mac NSMenu twin): each leaf sends
    /// <see cref="CalendarItemContextActionRequestedMessage"/>, which the calendar page ViewModel executes.
    /// </summary>
    public static UIMenu CreateMenu(IContextMenuItemService contextMenus, CalendarItemViewModel item)
    {
        var children = new List<UIMenuElement>();
        foreach (var entry in contextMenus.GetCalendarItemContextMenuItems(item.CalendarItem)) children.Add(Element(entry, item));
        return UIMenu.Create(item.Title ?? string.Empty, children.ToArray());
    }

    private static UIMenuElement Element(CalendarContextMenuItem entry, CalendarItemViewModel item)
    {
        var glyph = Glyph(entry.Action);
        UIImage? image = glyph == WinoIconGlyph.None ? null : WinoIcons.Image(glyph, 17);
        if (entry.HasChildren)
        {
            var children = new List<UIMenuElement>();
            foreach (var child in entry.Children) children.Add(Element(child, item));
            return UIMenu.Create(Label(entry.Action), image, UIMenuIdentifier.None, (UIMenuOptions)0, children.ToArray());
        }
        var action = entry.Action;
        var element = UIAction.Create(Label(action), image, null,
            _ => WeakReferenceMessenger.Default.Send(new CalendarItemContextActionRequestedMessage(item, action)));
        var attributes = IsEnabled(entry, item) ? (UIMenuElementAttributes)0 : UIMenuElementAttributes.Disabled;
        if (action.ActionType == CalendarContextMenuActionType.Delete) attributes |= UIMenuElementAttributes.Destructive;
        element.Attributes = attributes;
        return element;
    }
}
