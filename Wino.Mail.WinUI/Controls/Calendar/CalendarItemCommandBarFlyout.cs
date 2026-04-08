using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Messages;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;

namespace Wino.Calendar.Controls;

public partial class CalendarItemCommandBarFlyout : CommandBarFlyout
{
    private readonly ICalendarContextMenuItemService _contextMenuItemService;

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(CalendarItemViewModel), typeof(CalendarItemCommandBarFlyout), new PropertyMetadata(null, new PropertyChangedCallback(OnItemChanged)));

    public CalendarItemViewModel Item
    {
        get { return (CalendarItemViewModel)GetValue(ItemProperty); }
        set { SetValue(ItemProperty, value); }
    }

    public CalendarItemCommandBarFlyout()
    {
        _contextMenuItemService = WinoApplication.Current.Services.GetRequiredService<ICalendarContextMenuItemService>();
        Opening += FlyoutOpening;
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CalendarItemCommandBarFlyout flyout)
        {
            flyout.UpdateMenuItems();
        }
    }

    private void FlyoutOpening(object sender, object e) => UpdateMenuItems();

    private void UpdateMenuItems()
    {
        PrimaryCommands.Clear();
        SecondaryCommands.Clear();

        if (Item?.CalendarItem == null)
            return;

        var menuItems = _contextMenuItemService.GetContextMenuItems(Item.CalendarItem);

        foreach (var menuItem in menuItems)
        {
            var appBarButton = BuildAppBarButton(menuItem);

            if (menuItem.IsPrimary)
                PrimaryCommands.Add(appBarButton);
            else
                SecondaryCommands.Add(appBarButton);
        }
    }

    private AppBarButton BuildAppBarButton(CalendarContextMenuItem menuItem)
    {
        var button = new AppBarButton
        {
            Label = GetActionLabel(menuItem.Action),
            IsEnabled = menuItem.IsEnabled,
            Icon = new WinoFontIcon
            {
                Icon = GetActionIcon(menuItem.Action),
                FontSize = 16
            }
        };

        if (menuItem.HasChildren)
        {
            var flyout = new MenuFlyout();
            PopulateMenuFlyoutItems(flyout.Items, menuItem.Children);
            button.Flyout = flyout;
        }
        else
        {
            button.Click += (_, _) => ExecuteAction(menuItem.Action);
        }

        return button;
    }

    private void PopulateMenuFlyoutItems(IList<MenuFlyoutItemBase> items, IReadOnlyList<CalendarContextMenuItem> menuItems)
    {
        foreach (var menuItem in menuItems)
        {
            if (menuItem.HasChildren)
            {
                var subItem = new MenuFlyoutSubItem
                {
                    Text = GetActionLabel(menuItem.Action),
                    IsEnabled = menuItem.IsEnabled
                };

                PopulateMenuFlyoutItems(subItem.Items, menuItem.Children);
                items.Add(subItem);
            }
            else
            {
                var flyoutItem = new MenuFlyoutItem
                {
                    Text = GetActionLabel(menuItem.Action),
                    IsEnabled = menuItem.IsEnabled
                };

                flyoutItem.Click += (_, _) => ExecuteAction(menuItem.Action);
                items.Add(flyoutItem);
            }
        }
    }

    private void ExecuteAction(CalendarContextMenuAction action)
    {
        if (Item == null)
            return;

        WeakReferenceMessenger.Default.Send(new CalendarItemContextActionRequestedMessage(Item, action));
        Hide();
    }

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
