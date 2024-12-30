using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Collections;
using Wino.Core.Domain.Interfaces;


namespace Wino.Calendar.Controls
{
    public sealed partial class AllDayItemsControl : UserControl
    {
        private const string STATE_SummaryView = "SummaryView";
        private const string STATE_FullView = "FullView";

        #region Dependency Properties

        public static readonly DependencyProperty EventCollectionProperty = DependencyProperty.Register(nameof(EventCollection), typeof(CalendarEventCollection), typeof(AllDayItemsControl), new PropertyMetadata(null, new PropertyChangedCallback(OnAllDayEventsChanged)));
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

        private static void OnAllDayEventsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AllDayItemsControl control)
            {
                if (e.OldValue != null && e.OldValue is CalendarEventCollection oldCollection)
                {
                    control.UnregisterEventCollectionChanged(oldCollection);
                }

                if (e.NewValue != null && e.NewValue is CalendarEventCollection newCollection)
                {
                    control.RegisterEventCollectionChanged(newCollection);
                }

                control.UpdateCollectionVisuals();
            }
        }

        private void RegisterEventCollectionChanged(CalendarEventCollection collection)
        {
            collection.CalendarItemAdded += SingleEventUpdated;
            collection.CalendarItemRemoved += SingleEventUpdated;

            collection.CalendarItemsCleared += EventsCleared;
        }

        private void UnregisterEventCollectionChanged(CalendarEventCollection collection)
        {
            collection.CalendarItemAdded -= SingleEventUpdated;
            collection.CalendarItemRemoved -= SingleEventUpdated;

            collection.CalendarItemsCleared -= EventsCleared;
        }

        private void SingleEventUpdated(object sender, ICalendarItem calendarItem) => UpdateCollectionVisuals();
        private void EventsCleared(object sender, System.EventArgs e) => UpdateCollectionVisuals();

        private void UpdateCollectionVisuals()
        {
            if (EventCollection == null) return;

            if (EventCollection.AllDayEvents.Count > 1)
            {
                // Summarize
                VisualStateManager.GoToState(this, STATE_SummaryView, false);
            }
            else
            {
                // Full view.
                VisualStateManager.GoToState(this, STATE_FullView, false);
            }
        }
    }
}
