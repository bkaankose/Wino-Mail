using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml;
using Wino.Core.Domain.Interfaces;

namespace Wino.Controls
{
    public class AccountNavigationItem : WinoNavigationViewItem
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

            // Adjsuting Margin in the styles are not possible due to the fact that we use the same tempalte for different types of menu items.
            // Account templates listed under merged accounts will have Padding of 44. We must adopt to that.

            bool hasParentMenuItem = BindingData is IAccountMenuItem accountMenuItem && accountMenuItem.ParentMenuItem != null;

            _selectionIndicator.Margin = !hasParentMenuItem ? new Thickness(-44, 12, 0, 12) : new Thickness(-60, 12, -60, 12);
            _selectionIndicator.Scale = IsActiveAccount ? new Vector3(1, 1, 1) : new Vector3(0, 0, 0);
            _selectionIndicator.Visibility = IsActiveAccount ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
