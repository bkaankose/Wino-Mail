using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls
{
    public class CalendarDayItemsControl : Control
    {
        private const string PART_CalendarPanel = nameof(PART_CalendarPanel);

        private WinoCalendarPanel CalendarPanel;

        public CalendarDayModel DayModel
        {
            get { return (CalendarDayModel)GetValue(DayModelProperty); }
            set { SetValue(DayModelProperty, value); }
        }


        public static readonly DependencyProperty DayModelProperty = DependencyProperty.Register(nameof(DayModel), typeof(CalendarDayModel), typeof(CalendarDayItemsControl), new PropertyMetadata(null, new PropertyChangedCallback(OnRepresentingDateChanged)));

        private static void OnRepresentingDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CalendarDayItemsControl control)
            {
                if (e.OldValue != null && e.OldValue is CalendarDayModel oldCalendarDayModel)
                {
                    control.DetachCollection(oldCalendarDayModel.Events);
                }

                if (e.NewValue != null && e.NewValue is CalendarDayModel newCalendarDayModel)
                {
                    control.AttachCollection(newCalendarDayModel.Events);
                }

                control.ResetItems();
                control.RenderEvents();
            }
        }

        public CalendarDayItemsControl()
        {
            DefaultStyleKey = typeof(CalendarDayItemsControl);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            CalendarPanel = GetTemplateChild(PART_CalendarPanel) as WinoCalendarPanel;

            RenderEvents();
        }

        private void ResetItems()
        {
            if (CalendarPanel == null) return;

            CalendarPanel.Children.Clear();
        }

        private void RenderEvents()
        {
            if (CalendarPanel == null || CalendarPanel.DayModel == null) return;

            RenderCalendarItems();
        }

        private void AttachCollection(ObservableCollection<ICalendarItem> newCollection)
            => newCollection.CollectionChanged += CalendarItemsChanged;

        private void DetachCollection(ObservableCollection<ICalendarItem> oldCollection)
            => oldCollection.CollectionChanged -= CalendarItemsChanged;

        private void CalendarItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    foreach (ICalendarItem item in e.NewItems)
                    {
                        AddItem(item);
                    }
                    break;
                case NotifyCollectionChangedAction.Remove:
                    foreach (ICalendarItem item in e.OldItems)
                    {
                        var control = GetCalendarItemControl(item);
                        if (control != null)
                        {
                            CalendarPanel.Children.Remove(control);
                        }
                    }
                    break;
                case NotifyCollectionChangedAction.Reset:
                    ResetItems();
                    break;
                default:
                    break;
            }
        }

        private CalendarItemControl GetCalendarItemControl(ICalendarItem item)
            => CalendarPanel.Children.Where(c => c is CalendarItemControl calendarItemControl && calendarItemControl.Item == item).FirstOrDefault() as CalendarItemControl;

        private void RenderCalendarItems()
        {
            if (DayModel == null || DayModel.Events == null || DayModel.Events.Count == 0)
            {
                ResetItems();
                return;
            }

            foreach (var item in DayModel.Events)
            {
                AddItem(item);
            }
        }

        private void AddItem(ICalendarItem item)
        {
            CalendarPanel.Children.Add(new CalendarItemControl()
            {
                Item = item
            });
        }
    }
}
