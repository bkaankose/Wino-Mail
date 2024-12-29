using System.Collections.ObjectModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;


namespace Wino.Calendar.Controls
{
    public sealed partial class AllDayItemsControl : UserControl
    {
        private const string STATE_SummaryView = "SummaryView";
        private const string STATE_FullView = "FullView";

        #region Dependency Properties

        public static readonly DependencyProperty AllDayEventsProperty = DependencyProperty.Register(nameof(AllDayEvents), typeof(ObservableCollection<ICalendarItem>), typeof(AllDayItemsControl), new PropertyMetadata(null, new PropertyChangedCallback(OnAllDayEventsChanged)));
        public static readonly DependencyProperty AllDayEventTemplateProperty = DependencyProperty.Register(nameof(AllDayEventTemplate), typeof(DataTemplate), typeof(AllDayItemsControl), new PropertyMetadata(null));

        public DataTemplate AllDayEventTemplate
        {
            get { return (DataTemplate)GetValue(AllDayEventTemplateProperty); }
            set { SetValue(AllDayEventTemplateProperty, value); }
        }

        public ObservableCollection<ICalendarItem> AllDayEvents
        {
            get { return (ObservableCollection<ICalendarItem>)GetValue(AllDayEventsProperty); }
            set { SetValue(AllDayEventsProperty, value); }
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
                if (e.OldValue != null && e.OldValue is ObservableCollection<ICalendarItem> oldCollection)
                {
                    control.UnregisterEventCollectionChanged(oldCollection);
                }

                if (e.NewValue != null && e.NewValue is ObservableCollection<ICalendarItem> newCollection)
                {
                    control.RegisterEventCollectionChanged(newCollection);
                }

                control.UpdateCollectionVisuals();
            }
        }

        private void RegisterEventCollectionChanged(ObservableCollection<ICalendarItem> collection)
        {
            collection.CollectionChanged += EventsCollectionUpdated;
        }

        private void UnregisterEventCollectionChanged(ObservableCollection<ICalendarItem> collection)
        {
            collection.CollectionChanged -= EventsCollectionUpdated;
        }

        private void EventsCollectionUpdated(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateCollectionVisuals();
        }

        private void UpdateCollectionVisuals()
        {
            if (AllDayEvents == null) return;

            if (AllDayEvents.Count > 1)
            {
                // Summarize

                VisualStateManager.GoToState(this, STATE_SummaryView, false);

                // AllDayItemsSummaryButton.Content = $"{AllDayEvents.Count} {Translator.CalendarAllDayEventSummary}";
            }
            else
            {
                // Full view.
                VisualStateManager.GoToState(this, STATE_FullView, false);
            }
        }
    }
}
