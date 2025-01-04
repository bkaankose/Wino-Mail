using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls
{
    public sealed partial class CalendarItemControl : UserControl
    {
        public bool IsAllDayMultiDayEvent { get; set; }

        public static readonly DependencyProperty CalendarItemProperty = DependencyProperty.Register(nameof(CalendarItem), typeof(CalendarItemViewModel), typeof(CalendarItemControl), new PropertyMetadata(null, new PropertyChangedCallback(OnCalendarItemChanged)));
        public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(nameof(IsDragging), typeof(bool), typeof(CalendarItemControl), new PropertyMetadata(false));
        public static readonly DependencyProperty IsCustomEventAreaProperty = DependencyProperty.Register(nameof(IsCustomEventArea), typeof(bool), typeof(CalendarItemControl), new PropertyMetadata(false));
        public static readonly DependencyProperty CalendarItemTitleProperty = DependencyProperty.Register(nameof(CalendarItemTitle), typeof(string), typeof(CalendarItemControl), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty DisplayingDateProperty = DependencyProperty.Register(nameof(DisplayingDate), typeof(CalendarDayModel), typeof(CalendarItemControl), new PropertyMetadata(null, new PropertyChangedCallback(OnDisplayDateChanged)));

        /// <summary>
        /// Whether the control is displaying as regular event or all-multi day area in the day control.
        /// </summary>
        public bool IsCustomEventArea
        {
            get { return (bool)GetValue(IsCustomEventAreaProperty); }
            set { SetValue(IsCustomEventAreaProperty, value); }
        }

        /// <summary>
        /// Day that the calendar item is rendered at.
        /// It's needed for title manipulation and some other adjustments later on.
        /// </summary>
        public CalendarDayModel DisplayingDate
        {
            get { return (CalendarDayModel)GetValue(DisplayingDateProperty); }
            set { SetValue(DisplayingDateProperty, value); }
        }

        public string CalendarItemTitle
        {
            get { return (string)GetValue(CalendarItemTitleProperty); }
            set { SetValue(CalendarItemTitleProperty, value); }
        }

        public CalendarItemViewModel CalendarItem
        {
            get { return (CalendarItemViewModel)GetValue(CalendarItemProperty); }
            set { SetValue(CalendarItemProperty, value); }
        }

        public bool IsDragging
        {
            get { return (bool)GetValue(IsDraggingProperty); }
            set { SetValue(IsDraggingProperty, value); }
        }

        public CalendarItemControl()
        {
            InitializeComponent();
        }

        private static void OnDisplayDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CalendarItemControl control)
            {
                control.UpdateControlVisuals();
            }
        }

        private static void OnCalendarItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CalendarItemControl control)
            {
                control.UpdateControlVisuals();
            }
        }

        private void UpdateControlVisuals()
        {
            // Depending on the calendar item's duration and attributes, we might need to change the display title.
            // 1. Multi-Day events should display the start date and end date.
            // 2. Multi-Day events that occupy the whole day just shows 'all day'.
            // 3. Other events should display the title.

            if (CalendarItem == null) return;
            if (DisplayingDate == null) return;

            if (CalendarItem.IsMultiDayEvent)
            {
                // Multi day events are divided into 3 categories:
                // 1. All day events
                // 2. Events that started after the period.
                // 3. Events that started before the period and finishes within the period.

                var periodRelation = CalendarItem.Period.GetRelation(DisplayingDate.Period);

                if (periodRelation == Itenso.TimePeriod.PeriodRelation.StartInside ||
                    periodRelation == PeriodRelation.EnclosingStartTouching)
                {
                    // hour -> title
                    CalendarItemTitle = $"{DisplayingDate.CalendarRenderOptions.CalendarSettings.GetTimeString(CalendarItem.StartDate.TimeOfDay)} -> {CalendarItem.Title}";
                }
                else if (
                    periodRelation == PeriodRelation.EndInside ||
                    periodRelation == PeriodRelation.EnclosingEndTouching)
                {
                    // title <- hour
                    CalendarItemTitle = $"{CalendarItem.Title} <- {DisplayingDate.CalendarRenderOptions.CalendarSettings.GetTimeString(CalendarItem.EndDate.TimeOfDay)}";
                }
                else if (periodRelation == PeriodRelation.Enclosing)
                {
                    // This event goes all day and it's multi-day.
                    // Item must be hidden in the calendar but displayed on the custom area at the top.

                    CalendarItemTitle = $"{Translator.CalendarItemAllDay} {CalendarItem.Title}";
                }
                else
                {
                    // Not expected, but there it is.
                    CalendarItemTitle = CalendarItem.Title;
                }

                Debug.WriteLine($"{CalendarItem.Title} Period relation with {DisplayingDate.Period.ToString()}: {periodRelation}");
            }
            else
            {
                CalendarItemTitle = CalendarItem.Title;
            }

            UpdateVisualStates();
        }

        private void UpdateVisualStates()
        {
            if (CalendarItem == null) return;

            if (CalendarItem.IsAllDayEvent)
            {
                VisualStateManager.GoToState(this, "AllDayEvent", true);
            }
            else if (CalendarItem.IsMultiDayEvent)
            {
                if (IsCustomEventArea)
                {
                    VisualStateManager.GoToState(this, "CustomAreaMultiDayEvent", true);
                }
                else
                {
                    // Hide it.
                    VisualStateManager.GoToState(this, "MultiDayEvent", true);
                }
            }
            else
            {
                VisualStateManager.GoToState(this, "RegularEvent", true);
            }
        }

        private void ControlDragStarting(UIElement sender, DragStartingEventArgs args) => IsDragging = true;

        private void ControlDropped(UIElement sender, DropCompletedEventArgs args) => IsDragging = false;

        private void ControlTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (CalendarItem == null) return;

            WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(CalendarItem));
        }

        private void ControlDoubleTapped(object sender, Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (CalendarItem == null) return;

            WeakReferenceMessenger.Default.Send(new CalendarItemDoubleTappedMessage(CalendarItem));
        }

        private void ControlRightTapped(object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (CalendarItem == null) return;

            WeakReferenceMessenger.Default.Send(new CalendarItemRightTappedMessage(CalendarItem));
        }

        private void ContextFlyoutOpened(object sender, object e)
        {
            if (CalendarItem == null) return;

            if (!CalendarItem.IsSelected)
            {
                WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(CalendarItem));
            }
        }
    }
}
