using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Controls;

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
    }

    private void OnIsPaneOpenChanged(DependencyObject sender, DependencyProperty property)
    {
        UpdateAccountItems(_menuItemsHost);
        UpdateAccountItems(_footerMenuItemsHost);
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
