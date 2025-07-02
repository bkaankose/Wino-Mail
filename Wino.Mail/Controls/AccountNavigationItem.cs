using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP.Controls;

namespace Wino.Controls;

public partial class AccountNavigationItem : WinoNavigationViewItem
{

    public static readonly DependencyProperty IsActiveAccountProperty = DependencyProperty.Register(nameof(IsActiveAccount), typeof(bool), typeof(AccountNavigationItem), new PropertyMetadata(false, new PropertyChangedCallback(OnIsActiveAccountChanged)));
    public static readonly DependencyProperty BindingDataProperty = DependencyProperty.Register(nameof(BindingData), typeof(IAccountMenuItem), typeof(AccountNavigationItem), new PropertyMetadata(null));


    public bool IsActiveAccount
    {
        get { return (bool)GetValue(IsActiveAccountProperty); }
        set { SetValue(IsActiveAccountProperty, value); }
    }

    public IAccountMenuItem BindingData
    {
        get { return (IAccountMenuItem)GetValue(BindingDataProperty); }
        set { SetValue(BindingDataProperty, value); }
    }

    private const string PART_NavigationViewItemMenuItemsHost = "NavigationViewItemMenuItemsHost";
    private const string PART_SelectionIndicator = "CustomSelectionIndicator";

    private ItemsRepeater _itemsRepeater;
    private Windows.UI.Xaml.Shapes.Rectangle _selectionIndicator;

    public AccountNavigationItem()
    {
        DefaultStyleKey = typeof(AccountNavigationItem);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _itemsRepeater = GetTemplateChild(PART_NavigationViewItemMenuItemsHost) as ItemsRepeater;
        _selectionIndicator = GetTemplateChild(PART_SelectionIndicator) as Windows.UI.Xaml.Shapes.Rectangle;

        UpdateSelectionBorder();
    }

    private static void OnIsActiveAccountChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is AccountNavigationItem control)
            control.UpdateSelectionBorder();
    }

    private void UpdateSelectionBorder()
    {
        if (_selectionIndicator == null) return;

        _selectionIndicator.Scale = IsActiveAccount ? new Vector3(1, 1, 1) : new Vector3(0, 0, 0);
        _selectionIndicator.Visibility = IsActiveAccount ? Visibility.Visible : Visibility.Collapsed;
    }
}
