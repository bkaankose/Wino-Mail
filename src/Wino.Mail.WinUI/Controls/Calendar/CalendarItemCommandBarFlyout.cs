using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
using Wino.MenuFlyouts;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.WinUI.Controls;

namespace Wino.Calendar.Controls;

public partial class CalendarItemCommandBarFlyout : WinoContextFlyout
{
    private readonly ContextFlyoutShortcutResolver _shortcutResolver = new(
        WinoApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>());

    public CalendarItemViewModel? Item { get; set; }

    public void SetMenuItems(IReadOnlyList<CalendarContextMenuItem> menuItems)
    {
        // Submenus carry no command. Leaves capture the event before Closed clears Item.
        var item = Item;
        ItemsSource = item == null ? [] : menuItems.Select(entry => CreateEntry(entry, item)).ToArray();
    }

    private ContextFlyoutMenuEntry CreateEntry(CalendarContextMenuItem menuItem, CalendarItemViewModel item)
    {
        var action = menuItem.Action;
        var automationId = $"CalendarContext{action.ActionType}{action.TargetType}{action.ShowAs}{action.ResponseStatus}";
        var icon = CreateIcon(GetActionIcon(action));
        var isMutation = action.ActionType is CalendarContextMenuActionType.Delete or
            CalendarContextMenuActionType.ShowAs or CalendarContextMenuActionType.Respond;
        var isEnabled = menuItem.IsEnabled && !item.IsBusy &&
            (!isMutation || item.CalendarItem.AssignedCalendar?.IsReadOnly != true);

        if (menuItem.HasChildren)
        {
            return new ContextFlyoutSubMenuEntry
            {
                Text = GetActionLabel(action),
                Icon = icon,
                IsEnabled = isEnabled,
                AutomationId = automationId,
                Items = menuItem.Children.Select(child => CreateEntry(child, item)).ToArray()
            };
        }

        return new ContextFlyoutCommandEntry
        {
            Text = GetActionLabel(action),
            Icon = icon,
            IsEnabled = isEnabled,
            Shortcut = action.ActionType == CalendarContextMenuActionType.Delete && action.TargetType != CalendarEventTargetType.Series
                ? _shortcutResolver.Resolve(KeyboardShortcutAction.Delete, WinoApplicationMode.Calendar, KeyboardShortcutInputContext.Calendar)
                : null,
            IsDestructive = action.ActionType == CalendarContextMenuActionType.Delete,
            AutomationId = automationId,
            Command = new RelayCommand(
                () => WeakReferenceMessenger.Default.Send(new CalendarItemContextActionRequestedMessage(item, action)),
                () => isEnabled && !item.IsBusy)
        };
    }

#if DEBUG
    public void AddTestNotificationCommand(Func<Task> createNotificationAsync)
    {
        ItemsSource = (ContextFlyoutMenuEntry[])[.. ItemsSource ?? [], new ContextFlyoutCommandEntry
        {
            Text = Translator.Buttons_TestNotification,
            Icon = CreateIcon(WinoIconGlyph.Reminder),
            AutomationId = "CalendarEventTestNotification",
            Command = new AsyncRelayCommand(createNotificationAsync)
        }];
    }
#endif

    public void ClearMenuItems()
    {
        ItemsSource = [];
        HeaderItemsSource = [];
    }

    private static ContextFlyoutIcon? CreateIcon(WinoIconGlyph icon)
        => ControlConstants.WinoIconFontDictionary.TryGetValue(icon, out var glyph)
            ? new ContextFlyoutIcon(glyph)
            : null;

    private static string GetActionLabel(CalendarContextMenuAction action)
    {
        if (action.ShowAs.HasValue)
        {
            return action.ShowAs.Value switch
            {
                CalendarItemShowAs.Free => Translator.CalendarShowAs_Free,
                CalendarItemShowAs.Tentative => Translator.CalendarShowAs_Tentative,
                CalendarItemShowAs.Busy => Translator.CalendarShowAs_Busy,
                CalendarItemShowAs.OutOfOffice => Translator.CalendarShowAs_OutOfOffice,
                CalendarItemShowAs.WorkingElsewhere => Translator.CalendarShowAs_WorkingElsewhere,
                _ => Translator.CalendarShowAs_Busy
            };
        }

        if (action.ResponseStatus.HasValue)
        {
            return action.ResponseStatus.Value switch
            {
                CalendarItemStatus.Accepted => Translator.CalendarEventResponse_Accept,
                CalendarItemStatus.Tentative => Translator.CalendarEventResponse_Tentative,
                CalendarItemStatus.Cancelled => Translator.CalendarEventResponse_Decline,
                _ => Translator.CalendarEventResponse_Accept
            };
        }

        if (action.TargetType.HasValue && action.ActionType is CalendarContextMenuActionType.Delete or CalendarContextMenuActionType.ShowAs or CalendarContextMenuActionType.Respond)
        {
            return action.TargetType == CalendarEventTargetType.Single
                ? Translator.CalendarContextMenu_ThisEventOnly
                : Translator.CalendarContextMenu_AllEventsInSeries;
        }

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

    private static WinoIconGlyph GetActionIcon(CalendarContextMenuAction action)
        => action.ActionType switch
        {
            CalendarContextMenuActionType.Open when action.TargetType == CalendarEventTargetType.Series => WinoIconGlyph.EventEditSeries,
            CalendarContextMenuActionType.Open => WinoIconGlyph.OpenInNewWindow,
            CalendarContextMenuActionType.JoinOnline => WinoIconGlyph.EventJoinOnline,
            CalendarContextMenuActionType.Delete => WinoIconGlyph.Delete,
            CalendarContextMenuActionType.ShowAs => WinoIconGlyph.CalendarShowAs,
            CalendarContextMenuActionType.Respond => WinoIconGlyph.EventRespond,
            _ => WinoIconGlyph.More
        };
}
