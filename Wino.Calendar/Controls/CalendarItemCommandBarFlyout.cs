using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml;
using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.Controls
{
    public class CalendarItemCommandBarFlyout : CommandBarFlyout
    {
        public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(CalendarItemViewModel), typeof(CalendarItemCommandBarFlyout), new PropertyMetadata(null, new PropertyChangedCallback(OnItemChanged)));

        public CalendarItemViewModel Item
        {
            get { return (CalendarItemViewModel)GetValue(ItemProperty); }
            set { SetValue(ItemProperty, value); }
        }


        private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CalendarItemCommandBarFlyout flyout)
            {
                flyout.UpdateMenuItems();
            }
        }

        private void UpdateMenuItems()
        {

        }
    }
}
