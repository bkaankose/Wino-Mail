using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Controls;
using WinRT;
using Microsoft.UI.Xaml.Shapes;

namespace Wino.Controls;

public partial class AccountNavigationItem : WinoNavigationViewItem
{

    public static readonly DependencyProperty IsActiveAccountProperty = DependencyProperty.Register(nameof(IsActiveAccount), typeof(bool), typeof(AccountNavigationItem), new PropertyMetadata(false, new PropertyChangedCallback(OnIsActiveAccountChanged)));
    public static readonly DependencyProperty BindingDataProperty = DependencyProperty.Register(nameof(BindingData), typeof(IMenuItem), typeof(AccountNavigationItem), new PropertyMetadata(null));
    public static readonly DependencyProperty IconBoxHeightProperty = DependencyProperty.Register(nameof(IconBoxHeight), typeof(double), typeof(AccountNavigationItem), new PropertyMetadata(28d, new PropertyChangedCallback(OnIconBoxHeightChanged)));


    public bool IsActiveAccount
    {
        get { return (bool)GetValue(IsActiveAccountProperty); }
        set { SetValue(IsActiveAccountProperty, value); }
    }

    public IMenuItem BindingData
    {
        get { return (IMenuItem)GetValue(BindingDataProperty); }
        set { SetValue(BindingDataProperty, value); }
    }

    public double IconBoxHeight
    {
        get { return (double)GetValue(IconBoxHeightProperty); }
        set { SetValue(IconBoxHeightProperty, value); }
    }

    private const string PART_NavigationViewItemMenuItemsHost = "NavigationViewItemMenuItemsHost";
    private const string PART_SelectionIndicator = "CustomSelectionIndicator";
    private const string PART_NavigationViewItemPresenter = "NavigationViewItemPresenter";
    private const string PresenterContentGridName = "ContentGrid";
    private const string PresenterIconBoxName = "IconBox";

    // The presenter template's own right margin on its content grid while the pane is open.
    private static readonly Thickness OpenPaneContentGridMargin = new(0, 0, 14, 0);

    private ItemsRepeater _itemsRepeater = null!;
    private Microsoft.UI.Xaml.Shapes.Rectangle _selectionIndicator = null!;
    private NavigationViewItemPresenter? _presenter;
    private bool _isPaneClosed;

    public AccountNavigationItem()
    {
        DefaultStyleKey = typeof(AccountNavigationItem);
    }

    [DynamicWindowsRuntimeCast(typeof(ItemsRepeater))]
    [DynamicWindowsRuntimeCast(typeof(Rectangle))]
    [DynamicWindowsRuntimeCast(typeof(NavigationViewItemPresenter))]
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_presenter != null)
            _presenter.Loaded -= PresenterLoaded;

        _itemsRepeater = (GetTemplateChild(PART_NavigationViewItemMenuItemsHost) as ItemsRepeater)!;
        _selectionIndicator = (GetTemplateChild(PART_SelectionIndicator) as Microsoft.UI.Xaml.Shapes.Rectangle)!;
        _presenter = GetTemplateChild(PART_NavigationViewItemPresenter) as NavigationViewItemPresenter;

        // The presenter builds its own template later, so the content grid only exists once it loaded.
        if (_presenter != null)
            _presenter.Loaded += PresenterLoaded;

        UpdateSelectionBorder();
        UpdatePresenterContentGridMargin();
        UpdateIconBoxHeight();
    }

    /// <summary>
    /// The presenter keeps a 14 px right margin on its content grid and only drops it through its
    /// closed-pane visual state. When that state is missed, the 40 px wide closed pane leaves 26 px
    /// for the icon column and the 28 px account icon is clipped on the right. The margin is set
    /// here from the real pane state so the icon does not depend on that visual state.
    /// </summary>
    internal void SetPaneClosed(bool isPaneClosed)
    {
        _isPaneClosed = isPaneClosed;
        UpdatePresenterContentGridMargin();
    }

    private void PresenterLoaded(object sender, RoutedEventArgs e)
    {
        UpdatePresenterContentGridMargin();
        UpdateIconBoxHeight();
    }

    private void UpdatePresenterContentGridMargin()
    {
        if (_presenter == null || FindPresenterChild(_presenter, PresenterContentGridName) is not { } contentGrid)
            return;

        contentGrid.Margin = _isPaneClosed ? new Thickness(0) : OpenPaneContentGridMargin;
    }

    /// <summary>
    /// The item style fixes the presenter's icon box resource at 28 px for the account avatars, and the
    /// presenter reads it only while building its template. A smaller glyph needs the box itself resized.
    /// </summary>
    private void UpdateIconBoxHeight()
    {
        if (_presenter == null || FindPresenterChild(_presenter, PresenterIconBoxName) is not { } iconBox)
            return;

        iconBox.Height = IconBoxHeight;
    }

    private static FrameworkElement? FindPresenterChild(DependencyObject parent, string name)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);

        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);

            if (child is FrameworkElement element && element.Name == name)
                return element;

            // The icon box sits in a Border inside the content grid, so every child is searched.
            if (FindPresenterChild(child, name) is { } nested)
                return nested;
        }

        return null;
    }

    private static void OnIconBoxHeightChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is AccountNavigationItem control)
            control.UpdateIconBoxHeight();
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
