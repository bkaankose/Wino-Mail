using System.Windows.Input;
using CommunityToolkit.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Mail.WinUI.Controls;

namespace Wino.Calendar.Controls;

public partial class WinoCalendarTypeSelectorControl : Control
{
    private const string PART_TodayButton = nameof(PART_TodayButton);
    private const string PART_DayToggle = nameof(PART_DayToggle);
    private const string PART_WeekToggle = nameof(PART_WeekToggle);
    private const string PART_WorkWeekToggle = nameof(PART_WorkWeekToggle);
    private const string PART_MonthToggle = nameof(PART_MonthToggle);

    public static readonly DependencyProperty SelectedTypeProperty = DependencyProperty.Register(
        nameof(SelectedType),
        typeof(CalendarDisplayType),
        typeof(WinoCalendarTypeSelectorControl),
        new PropertyMetadata(CalendarDisplayType.Week, OnSelectedTypeChanged));
    public static readonly DependencyProperty DisplayDayCountProperty = DependencyProperty.Register(nameof(DisplayDayCount), typeof(int), typeof(WinoCalendarTypeSelectorControl), new PropertyMetadata(0));
    public static readonly DependencyProperty TodayClickedCommandProperty = DependencyProperty.Register(nameof(TodayClickedCommand), typeof(ICommand), typeof(WinoCalendarTypeSelectorControl), new PropertyMetadata(null));

    public ICommand? TodayClickedCommand
    {
        get { return (ICommand?)GetValue(TodayClickedCommandProperty); }
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

    private AppBarButton? _todayButton;
    private AppBarToggleButton? _dayToggle;
    private AppBarToggleButton? _weekToggle;
    private AppBarToggleButton? _workWeekToggle;
    private AppBarToggleButton? _monthToggle;

    public WinoCalendarTypeSelectorControl()
    {
        DefaultStyleKey = typeof(WinoCalendarTypeSelectorControl);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        UnregisterHandlers();

        _todayButton = GetTemplateChild(PART_TodayButton) as AppBarButton;
        _dayToggle = GetTemplateChild(PART_DayToggle) as AppBarToggleButton;
        _weekToggle = GetTemplateChild(PART_WeekToggle) as AppBarToggleButton;
        _workWeekToggle = GetTemplateChild(PART_WorkWeekToggle) as AppBarToggleButton;
        _monthToggle = GetTemplateChild(PART_MonthToggle) as AppBarToggleButton;

        Guard.IsNotNull(_todayButton, nameof(_todayButton));
        Guard.IsNotNull(_dayToggle, nameof(_dayToggle));
        Guard.IsNotNull(_weekToggle, nameof(_weekToggle));
        Guard.IsNotNull(_workWeekToggle, nameof(_workWeekToggle));
        Guard.IsNotNull(_monthToggle, nameof(_monthToggle));

        _todayButton!.Click += TodayClicked;
        _dayToggle!.Click += DayToggleClicked;
        _weekToggle!.Click += WeekToggleClicked;
        _workWeekToggle!.Click += WorkWeekToggleClicked;
        _monthToggle!.Click += MonthToggleClicked;

        UpdateToggleButtonStates();
    }

    private void TodayClicked(object? sender, RoutedEventArgs e) => TodayClickedCommand?.Execute(null);

    private void DayToggleClicked(object sender, RoutedEventArgs e) => SetSelectedType(CalendarDisplayType.Day);

    private void WeekToggleClicked(object sender, RoutedEventArgs e) => SetSelectedType(CalendarDisplayType.Week);

    private void WorkWeekToggleClicked(object sender, RoutedEventArgs e) => SetSelectedType(CalendarDisplayType.WorkWeek);

    private void MonthToggleClicked(object sender, RoutedEventArgs e) => SetSelectedType(CalendarDisplayType.Month);

    private void SetSelectedType(CalendarDisplayType type)
    {
        SelectedType = type;
        UpdateToggleButtonStates();
    }

    private static void OnSelectedTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WinoCalendarTypeSelectorControl control)
        {
            control.UpdateToggleButtonStates();
        }
    }

    private void UnregisterHandlers()
    {
        if (_todayButton != null)
        {
            _todayButton.Click -= TodayClicked;
        }

        if (_dayToggle != null)
        {
            _dayToggle.Click -= DayToggleClicked;
        }

        if (_weekToggle != null)
        {
            _weekToggle.Click -= WeekToggleClicked;
        }

        if (_workWeekToggle != null)
        {
            _workWeekToggle.Click -= WorkWeekToggleClicked;
        }

        if (_monthToggle != null)
        {
            _monthToggle.Click -= MonthToggleClicked;
        }
    }

    private void UpdateToggleButtonStates()
    {
        if (_dayToggle == null || _weekToggle == null || _workWeekToggle == null || _monthToggle == null)
        {
            return;
        }

        _dayToggle.IsChecked = SelectedType == CalendarDisplayType.Day;
        _weekToggle.IsChecked = SelectedType == CalendarDisplayType.Week;
        _workWeekToggle.IsChecked = SelectedType == CalendarDisplayType.WorkWeek;
        _monthToggle.IsChecked = SelectedType == CalendarDisplayType.Month;
    }
}
