using System.Diagnostics;
using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml;

namespace Wino.Controls
{
    public class AccountNavigationItem : WinoNavigationViewItem
    {
        public bool IsActiveAccount
        {
            get { return (bool)GetValue(IsActiveAccountProperty); }
            set { SetValue(IsActiveAccountProperty, value); }
        }

        public static readonly DependencyProperty IsActiveAccountProperty = DependencyProperty.Register(nameof(IsActiveAccount), typeof(bool), typeof(AccountNavigationItem), new PropertyMetadata(false, new PropertyChangedCallback(OnIsActiveAccountChanged)));


        private const string PART_NavigationViewItemMenuItemsHost = "NavigationViewItemMenuItemsHost";
        private const string PART_SelectionIndicator = "SelectionIndicator";

        private ItemsRepeater _itemsRepeater;
        private Windows.UI.Xaml.Shapes.Rectangle _selectionIndicator;

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _itemsRepeater = GetTemplateChild(PART_NavigationViewItemMenuItemsHost) as ItemsRepeater;
            _selectionIndicator = GetTemplateChild(PART_SelectionIndicator) as Windows.UI.Xaml.Shapes.Rectangle;

            if (_itemsRepeater == null) return;

            (_itemsRepeater.Layout as StackLayout).Spacing = 0;

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

            _selectionIndicator.Scale = IsActiveAccount ? new Vector3(1,1,1) : new Vector3(0,0,0);
            _selectionIndicator.Visibility = IsActiveAccount ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
