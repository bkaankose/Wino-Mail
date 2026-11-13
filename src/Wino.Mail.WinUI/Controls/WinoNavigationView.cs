using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Controls;
using Wino.Core.Domain.Interfaces;
using WinRT;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// NavigationView that refreshes recycled menu item containers once they are prepared.
/// </summary>
/// <remarks>
/// The menu ItemsRepeater recycles containers when a mode swaps the menu. While a container is parked,
/// NavigationView marks it as not top-level but the item still tracks pane open/close, so a toggle in that
/// window stores the open-pane presenter layout (14 px right margin). Re-preparing the container restores
/// the top-level flag without refreshing that layout, and large icons such as account avatars are clipped
/// on the right in the closed pane. Account items are also told the pane state directly, because that
/// refresh still depends on what the item itself recorded about the pane.
/// </remarks>
public partial class WinoNavigationView : NavigationView
{
    private const string PART_MenuItemsHost = "MenuItemsHost";
    private const string PART_FooterMenuItemsHost = "FooterMenuItemsHost";

    private ItemsRepeater? _menuItemsHost;
    private ItemsRepeater? _footerMenuItemsHost;

    public WinoNavigationView()
    {
        RegisterPropertyChangedCallback(IsPaneOpenProperty, OnIsPaneOpenChanged);
    }

    [DynamicWindowsRuntimeCast(typeof(ItemsRepeater))]
    protected override void OnApplyTemplate()
    {
        DetachRepeaters();

        // NavigationView subscribes its own ElementPrepared handlers here. Subscribing after it
        // guarantees the top-level flag and position are assigned before the refresh runs.
        base.OnApplyTemplate();

        _menuItemsHost = GetTemplateChild(PART_MenuItemsHost) as ItemsRepeater;
        _footerMenuItemsHost = GetTemplateChild(PART_FooterMenuItemsHost) as ItemsRepeater;

        if (_menuItemsHost != null)
            _menuItemsHost.ElementPrepared += RepeaterElementPrepared;

        if (_footerMenuItemsHost != null)
            _footerMenuItemsHost.ElementPrepared += RepeaterElementPrepared;
    }

    private void DetachRepeaters()
    {
        if (_menuItemsHost != null)
            _menuItemsHost.ElementPrepared -= RepeaterElementPrepared;

        if (_footerMenuItemsHost != null)
            _footerMenuItemsHost.ElementPrepared -= RepeaterElementPrepared;

        _menuItemsHost = null;
        _footerMenuItemsHost = null;
    }

    private void RepeaterElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is WinoNavigationViewItem item)
            item.RefreshVisualState();

        if (args.Element is AccountNavigationItem accountItem)
            accountItem.SetPaneClosed(!IsPaneOpen);

        if (args.Element is WinoNavigationViewItem { RemembersExpansion: true } groupItem)
            ApplyRememberedExpansion(groupItem, isPrepared: true);
    }

    private void OnIsPaneOpenChanged(DependencyObject sender, DependencyProperty property)
    {
        UpdateAccountItems(_menuItemsHost);
        UpdateAccountItems(_footerMenuItemsHost);

        // After the pane's own layout change, so the restored rows show their children.
        if (IsPaneOpen)
            DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, ApplyRememberedExpansions);
        else
            ApplyRememberedExpansions();
    }

    private void ApplyRememberedExpansions()
    {
        if (_menuItemsHost?.ItemsSourceView is not { } items)
            return;

        for (var index = 0; index < items.Count; index++)
        {
            if (_menuItemsHost.TryGetElement(index) is WinoNavigationViewItem { RemembersExpansion: true } groupItem)
                ApplyRememberedExpansion(groupItem, isPrepared: false);
        }
    }

    /// <summary>
    /// An account group row shows its remembered expansion in the open pane. The closed pane keeps it collapsed,
    /// because an expanded row there shows its chevron without its children.
    /// </summary>
    private void ApplyRememberedExpansion(WinoNavigationViewItem groupItem, bool isPrepared)
    {
        if (groupItem.DataContext is not IMenuItem menuItem)
            return;

        var isExpanded = IsPaneOpen && menuItem.IsExpanded;

        // A container parked by a mode switch still follows its item's expansion, but NavigationView does not show or
        // hide the children of a parked container. Toggling it once in the open pane makes the children match again.
        if (isPrepared && IsPaneOpen && groupItem.IsExpanded == isExpanded)
            groupItem.IsExpanded = !isExpanded;

        if (groupItem.IsExpanded != isExpanded)
            groupItem.IsExpanded = isExpanded;
    }

    private void UpdateAccountItems(ItemsRepeater? repeater)
    {
        if (repeater?.ItemsSourceView is not { } items)
            return;

        for (var index = 0; index < items.Count; index++)
        {
            if (repeater.TryGetElement(index) is AccountNavigationItem accountItem)
                accountItem.SetPaneClosed(!IsPaneOpen);
        }
    }
}
