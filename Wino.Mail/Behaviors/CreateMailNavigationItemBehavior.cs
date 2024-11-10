using System.Collections.ObjectModel;
using Microsoft.Xaml.Interactivity;
using Windows.UI.Xaml;
using Wino.Core.MenuItems;
using Wino.Core.UWP.Controls;

namespace Wino.Behaviors
{
    public class CreateMailNavigationItemBehavior : Behavior<WinoNavigationViewItem>
    {
        public MenuItemBase SelectedMenuItem
        {
            get { return (MenuItemBase)GetValue(SelectedMenuItemProperty); }
            set { SetValue(SelectedMenuItemProperty, value); }
        }

        public ObservableCollection<MenuItemBase> MenuItems
        {
            get { return (ObservableCollection<MenuItemBase>)GetValue(MenuItemsProperty); }
            set { SetValue(MenuItemsProperty, value); }
        }

        public static readonly DependencyProperty MenuItemsProperty = DependencyProperty.Register(nameof(MenuItems), typeof(ObservableCollection<MenuItemBase>), typeof(CreateMailNavigationItemBehavior), new PropertyMetadata(null, OnMenuItemsChanged));
        public static readonly DependencyProperty SelectedMenuItemProperty = DependencyProperty.Register(nameof(SelectedMenuItem), typeof(MenuItemBase), typeof(CreateMailNavigationItemBehavior), new PropertyMetadata(null, OnSelectedMenuItemChanged));

        public CreateMailNavigationItemBehavior()
        {

        }

        protected override void OnAttached()
        {
            base.OnAttached();
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
        }

        private static void OnMenuItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs dependencyPropertyChangedEventArgs)
        {
            if (dependencyObject is CreateMailNavigationItemBehavior behavior)
            {
                if (dependencyPropertyChangedEventArgs.NewValue != null)
                    behavior.RegisterMenuItemChanges();

                behavior.ManageAccounts();
            }
        }

        private void RegisterMenuItemChanges()
        {
            if (MenuItems != null)
            {
                MenuItems.CollectionChanged -= MenuCollectionUpdated;
                MenuItems.CollectionChanged += MenuCollectionUpdated;
            }
        }

        private void MenuCollectionUpdated(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            ManageAccounts();
        }

        private static void OnSelectedMenuItemChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs dependencyPropertyChangedEventArgs)
        {
            if (dependencyObject is CreateMailNavigationItemBehavior behavior)
            {
                behavior.ManageAccounts();
            }
        }

        private void ManageAccounts()
        {
            if (MenuItems == null) return;

            AssociatedObject.MenuItems.Clear();

            if (SelectedMenuItem == null)
            {
                // ??
            }
        }
    }
}
