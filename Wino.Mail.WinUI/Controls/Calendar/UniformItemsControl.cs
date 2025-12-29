using System.Collections.Generic;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Calendar.Controls;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.WinUI.Controls.Calendar;

/// <summary>
/// AOT-Safe ItemsControl for use in UniformGrid panels.
/// </summary>
///
public partial class UniformItemsControl : Grid
{
    [GeneratedDependencyProperty]
    public partial DayRangeRenderModel? RenderModel { get; set; }

    [GeneratedDependencyProperty]
    public partial List<CalendarDayModel>? ItemsSource { get; set; }

    partial void OnRenderModelChanged(DayRangeRenderModel? newValue)
    {
        if (newValue == null || ItemsSource == null) return;

        AdjustColumns();
    }

    partial void OnItemsSourceChanged(List<CalendarDayModel>? newValue)
    {
        if (newValue == null || ItemsSource == null) return;

        AdjustColumns();
    }

    private void AdjustColumns()
    {
        if (RenderModel == null || ItemsSource == null) return;

        Children.Clear();
        ColumnDefinitions.Clear();

        var columns = RenderModel.TotalDays;

        // First divide.
        for (int i = 0; i < columns; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Then add items.
        for (int i = 0; i < columns; i++)
        {
            var item = ItemsSource[i];

            var control = new DayColumnControl()
            {
                DayModel = item
            };

            SetColumn(control, i);
            Children.Add(control);
        }
    }
}
//public partial class UniformItemsControl : ItemsControl
//{
//    private const string ControlUniformGridName = "PART_UniformGrid";

//    [GeneratedDependencyProperty]
//    public partial DayRangeRenderModel? RenderModel { get; set; }

//    partial void OnRenderModelChanged(DayRangeRenderModel? newValue)
//    {
//        if (newValue == null) return;

//        // Adjust the ItemsPanel based on the RenderModel's columns.
//        var uniGrid = WinoVisualTreeHelper.FindDescendants<UniformGrid>(this);

//        //if (uniGrid != null)
//        //{
//        //    uniGrid.Columns = newValue.TotalDays;
//        //}
//    }
//}
