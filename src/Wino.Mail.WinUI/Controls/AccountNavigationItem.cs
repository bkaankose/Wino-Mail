using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Controls;

namespace Wino.Controls;

public partial class AccountNavigationItem : WinoNavigationViewItem
{

    public static readonly DependencyProperty IsActiveAccountProperty = DependencyProperty.Register(nameof(IsActiveAccount), typeof(bool), typeof(AccountNavigationItem), new PropertyMetadata(false, new PropertyChangedCallback(OnIsActiveAccountChanged)));
    public static readonly DependencyProperty BindingDataProperty = DependencyProperty.Register(nameof(BindingData), typeof(IMenuItem), typeof(AccountNavigationItem), new PropertyMetadata(null));


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

    private const string PART_NavigationViewItemMenuItemsHost = "NavigationViewItemMenuItemsHost";
    private const string PART_SelectionIndicator = "CustomSelectionIndicator";
    private const string PART_NavigationViewItemPresenter = "NavigationViewItemPresenter";
    private const string PresenterContentGridName = "ContentGrid";

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

    private void PresenterLoaded(object sender, RoutedEventArgs e) => UpdatePresenterContentGridMargin();

    private void UpdatePresenterContentGridMargin()
    {
        if (_presenter == null || FindContentGrid(_presenter) is not { } contentGrid)
            return;

        contentGrid.Margin = _isPaneClosed ? new Thickness(0) : OpenPaneContentGridMargin;
    }

    private static FrameworkElement? FindContentGrid(DependencyObject parent)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);

        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);

            if (child is FrameworkElement { Name: PresenterContentGridName } contentGrid)
                return contentGrid;

            // The content grid sits two panels below the presenter. Icons and content hang below it.
            if (child is Panel && FindContentGrid(child) is { } nested)
                return nested;
        }

        return null;
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
