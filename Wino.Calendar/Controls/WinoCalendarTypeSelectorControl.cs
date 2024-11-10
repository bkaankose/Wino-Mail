using System.Windows.Input;
using CommunityToolkit.Diagnostics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Calendar.Controls
{
    public class WinoCalendarTypeSelectorControl : Control
    {
        private const string PART_TodayButton = nameof(PART_TodayButton);
        private const string PART_DayToggle = nameof(PART_DayToggle);
        private const string PART_WeekToggle = nameof(PART_WeekToggle);
        private const string PART_MonthToggle = nameof(PART_MonthToggle);
        private const string PART_YearToggle = nameof(PART_YearToggle);

        public static readonly DependencyProperty SelectedTypeProperty = DependencyProperty.Register(nameof(SelectedType), typeof(CalendarDisplayType), typeof(WinoCalendarTypeSelectorControl), new PropertyMetadata(CalendarDisplayType.Week));
        public static readonly DependencyProperty DisplayDayCountProperty = DependencyProperty.Register(nameof(DisplayDayCount), typeof(int), typeof(WinoCalendarTypeSelectorControl), new PropertyMetadata(0));
        public static readonly DependencyProperty TodayClickedCommandProperty = DependencyProperty.Register(nameof(TodayClickedCommand), typeof(ICommand), typeof(WinoCalendarTypeSelectorControl), new PropertyMetadata(null));

        public ICommand TodayClickedCommand
        {
            get { return (ICommand)GetValue(TodayClickedCommandProperty); }
            set { SetValue(TodayClickedCommandProperty, value); }
        }

        public CalendarDisplayType SelectedType
        {
            get { return (CalendarDisplayType)GetValue(SelectedTypeProperty); }
            set { SetValue(SelectedTypeProperty, value); }
        }

        public int DisplayDayCount
        {
            get { return (int)GetValue(DisplayDayCountProperty); }
            set { SetValue(DisplayDayCountProperty, value); }
        }

        private AppBarButton _todayButton;
        private AppBarToggleButton _dayToggle;
        private AppBarToggleButton _weekToggle;
        private AppBarToggleButton _monthToggle;
        private AppBarToggleButton _yearToggle;

        public WinoCalendarTypeSelectorControl()
        {
            DefaultStyleKey = typeof(WinoCalendarTypeSelectorControl);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _todayButton = GetTemplateChild(PART_TodayButton) as AppBarButton;
            _dayToggle = GetTemplateChild(PART_DayToggle) as AppBarToggleButton;
            _weekToggle = GetTemplateChild(PART_WeekToggle) as AppBarToggleButton;
            _monthToggle = GetTemplateChild(PART_MonthToggle) as AppBarToggleButton;
            _yearToggle = GetTemplateChild(PART_YearToggle) as AppBarToggleButton;

            Guard.IsNotNull(_todayButton, nameof(_todayButton));
            Guard.IsNotNull(_dayToggle, nameof(_dayToggle));
            Guard.IsNotNull(_weekToggle, nameof(_weekToggle));
            Guard.IsNotNull(_monthToggle, nameof(_monthToggle));
            Guard.IsNotNull(_yearToggle, nameof(_yearToggle));

            _todayButton.Click += TodayClicked;

            _dayToggle.Click += (s, e) => { SetSelectedType(CalendarDisplayType.Day); };
            _weekToggle.Click += (s, e) => { SetSelectedType(CalendarDisplayType.Week); };
            _monthToggle.Click += (s, e) => { SetSelectedType(CalendarDisplayType.Month); };
            _yearToggle.Click += (s, e) => { SetSelectedType(CalendarDisplayType.Year); };

            UpdateToggleButtonStates();
        }

        private void TodayClicked(object sender, RoutedEventArgs e) => TodayClickedCommand?.Execute(null);

        private void SetSelectedType(CalendarDisplayType type)
        {
            SelectedType = type;
            UpdateToggleButtonStates();
        }

        private void UpdateToggleButtonStates()
        {
            _dayToggle.IsChecked = SelectedType == CalendarDisplayType.Day;
            _weekToggle.IsChecked = SelectedType == CalendarDisplayType.Week;
            _monthToggle.IsChecked = SelectedType == CalendarDisplayType.Month;
            _yearToggle.IsChecked = SelectedType == CalendarDisplayType.Year;
        }
    }
}
