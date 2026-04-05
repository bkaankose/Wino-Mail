using System;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Calendar.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class CalendarTitleBarContent : UserControl
{
    public event EventHandler? PreviousDateRequested;
    public event EventHandler? NextDateRequested;

    public CalendarTitleBarContent()
    {
        InitializeComponent();
    }

    public string VisibleDateRangeText
    {
        get => VisibleDateRangeTextBlock.Text;
        set => VisibleDateRangeTextBlock.Text = value;
    }

    public ICommand? TodayClickedCommand
    {
        get => CalendarTypeSelector.TodayClickedCommand;
        set => CalendarTypeSelector.TodayClickedCommand = value;
    }

    public int DisplayDayCount
    {
        get => CalendarTypeSelector.DisplayDayCount;
        set => CalendarTypeSelector.DisplayDayCount = value;
    }

    public CalendarDisplayType SelectedType
    {
        get => CalendarTypeSelector.SelectedType;
        set => CalendarTypeSelector.SelectedType = value;
    }

    public long RegisterSelectedTypeChanged(DependencyPropertyChangedCallback callback)
        => CalendarTypeSelector.RegisterPropertyChangedCallback(WinoCalendarTypeSelectorControl.SelectedTypeProperty, callback);

    public void UnregisterSelectedTypeChanged(long token)
        => CalendarTypeSelector.UnregisterPropertyChangedCallback(WinoCalendarTypeSelectorControl.SelectedTypeProperty, token);

    private void PreviousDateClicked(object sender, RoutedEventArgs e)
        => PreviousDateRequested?.Invoke(this, EventArgs.Empty);

    private void NextDateClicked(object sender, RoutedEventArgs e)
        => NextDateRequested?.Invoke(this, EventArgs.Empty);
}
