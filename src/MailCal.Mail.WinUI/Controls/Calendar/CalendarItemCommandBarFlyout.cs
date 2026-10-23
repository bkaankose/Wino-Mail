using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MailCal.Core.Domain.Interfaces;
using MailCal.Mail.WinUI;
using MailCal.MenuFlyouts;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Calendar.ViewModels.Messages;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Mail.Controls.ContextFlyout;
using MailCal.Mail.Controls.Core.ContextFlyout;
using MailCal.Mail.WinUI.Controls;

namespace MailCal.Calendar.Controls;

public partial class CalendarItemCommandBarFlyout : MailCalContextFlyout
{
    private readonly ContextFlyoutShortcutResolver _shortcutResolver = new(
        MailCalApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>());

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
                ? _shortcutResolver.Resolve(KeyboardShortcutAction.Delete, MailCalApplicationMode.Calendar, KeyboardShortcutInputContext.Calendar)
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
            Icon = CreateIcon(MailCalIconGlyph.Reminder),
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

    private static ContextFlyoutIcon? CreateIcon(MailCalIconGlyph icon)
        => icon == MailCalIconGlyph.None
            ? null
            : new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(icon));

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

    private static MailCalIconGlyph GetActionIcon(CalendarContextMenuAction action)
        => action.ActionType switch
        {
            CalendarContextMenuActionType.Open when action.TargetType == CalendarEventTargetType.Series => MailCalIconGlyph.EventEditSeries,
            CalendarContextMenuActionType.Open => MailCalIconGlyph.OpenInNewWindow,
            CalendarContextMenuActionType.JoinOnline => MailCalIconGlyph.EventJoinOnline,
            CalendarContextMenuActionType.Delete => MailCalIconGlyph.Delete,
            CalendarContextMenuActionType.ShowAs => MailCalIconGlyph.CalendarShowAs,
            CalendarContextMenuActionType.Respond => MailCalIconGlyph.EventRespond,
            _ => MailCalIconGlyph.More
        };
}
