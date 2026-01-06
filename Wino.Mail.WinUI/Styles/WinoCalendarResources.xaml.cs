using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Calendar.Controls;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Styles;

public sealed partial class WinoCalendarResources : ResourceDictionary
{
    public WinoCalendarResources()
    {
        this.InitializeComponent();
    }

    private void OnRegularEventItemsControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsControl itemsControl && itemsControl.DataContext is CalendarDayModel dayModel)
        {
            if (itemsControl.ItemsPanelRoot is WinoCalendarPanel panel)
            {
                panel.HourHeight = dayModel.CalendarRenderOptions.CalendarSettings.HourHeight;
                panel.Period = dayModel.Period;
            }
        }
    }

    private void OnDayColumnsItemsControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsControl itemsControl && itemsControl.DataContext is DayRangeRenderModel rangeModel)
        {
            if (itemsControl.ItemsPanelRoot is UniformGrid uniformGrid)
            {
                uniformGrid.Columns = rangeModel.CalendarRenderOptions.TotalDayCount;
            }
        }
    }

    private void OnEventGridsItemsControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsControl itemsControl && itemsControl.DataContext is DayRangeRenderModel rangeModel)
        {
            if (itemsControl.ItemsPanelRoot is UniformGrid uniformGrid)
            {
                uniformGrid.Columns = rangeModel.CalendarRenderOptions.TotalDayCount;
            }
        }
    }
}
