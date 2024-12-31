using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Collections;


namespace Wino.Calendar.Controls
{
    public sealed partial class AllDayItemsControl : UserControl
    {
        #region Dependency Properties

        public static readonly DependencyProperty EventCollectionProperty = DependencyProperty.Register(nameof(EventCollection), typeof(CalendarEventCollection), typeof(AllDayItemsControl), new PropertyMetadata(null));
        public static readonly DependencyProperty AllDayEventTemplateProperty = DependencyProperty.Register(nameof(AllDayEventTemplate), typeof(DataTemplate), typeof(AllDayItemsControl), new PropertyMetadata(null));
        public static readonly DependencyProperty RegularEventItemTemplateProperty = DependencyProperty.Register(nameof(RegularEventItemTemplate), typeof(DataTemplate), typeof(AllDayItemsControl), new PropertyMetadata(null));

        /// <summary>
        /// Item template for all-day events to display in summary view area.
        /// More than 2 events will be shown in Flyout.
        /// </summary>
        public DataTemplate AllDayEventTemplate
        {
            get { return (DataTemplate)GetValue(AllDayEventTemplateProperty); }
            set { SetValue(AllDayEventTemplateProperty, value); }
        }

        /// <summary>
        /// Item template for all-day events to display in summary view's Flyout.
        /// </summary>
        public DataTemplate RegularEventItemTemplate
        {
            get { return (DataTemplate)GetValue(RegularEventItemTemplateProperty); }
            set { SetValue(RegularEventItemTemplateProperty, value); }
        }

        /// <summary>
        /// Whole collection of events to display.
        /// </summary>
        public CalendarEventCollection EventCollection
        {
            get { return (CalendarEventCollection)GetValue(EventCollectionProperty); }
            set { SetValue(EventCollectionProperty, value); }
        }

        #endregion

        public AllDayItemsControl()
        {
            InitializeComponent();
        }
    }
}
