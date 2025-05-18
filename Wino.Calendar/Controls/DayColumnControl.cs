using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public partial class DayColumnControl : Control
{
    private const string PART_HeaderDateDayText = nameof(PART_HeaderDateDayText);
    private const string PART_IsTodayBorder = nameof(PART_IsTodayBorder);
    private const string PART_ColumnHeaderText = nameof(PART_ColumnHeaderText);

    private const string PART_AllDayItemsControl = nameof(PART_AllDayItemsControl);

    private const string TodayState = nameof(TodayState);
    private const string NotTodayState = nameof(NotTodayState);

    private TextBlock HeaderDateDayText;
    private TextBlock ColumnHeaderText;
    private Border IsTodayBorder;
    private ItemsControl AllDayItemsControl;

    public CalendarDayModel DayModel
    {
        get { return (CalendarDayModel)GetValue(DayModelProperty); }
        set { SetValue(DayModelProperty, value); }
    }

    public static readonly DependencyProperty DayModelProperty = DependencyProperty.Register(nameof(DayModel), typeof(CalendarDayModel), typeof(DayColumnControl), new PropertyMetadata(null, new PropertyChangedCallback(OnRenderingPropertiesChanged)));

    public DayColumnControl()
    {
        DefaultStyleKey = typeof(DayColumnControl);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        HeaderDateDayText = GetTemplateChild(PART_HeaderDateDayText) as TextBlock;
        ColumnHeaderText = GetTemplateChild(PART_ColumnHeaderText) as TextBlock;
        IsTodayBorder = GetTemplateChild(PART_IsTodayBorder) as Border;
        AllDayItemsControl = GetTemplateChild(PART_AllDayItemsControl) as ItemsControl;

        UpdateValues();
    }

    private static void OnRenderingPropertiesChanged(DependencyObject control, DependencyPropertyChangedEventArgs e)
    {
        if (control is DayColumnControl columnControl)
        {
            columnControl.UpdateValues();
        }
    }

    private void UpdateValues()
    {
        if (HeaderDateDayText == null || IsTodayBorder == null || DayModel == null) return;

        HeaderDateDayText.Text = DayModel.RepresentingDate.Day.ToString();

        // Monthly template does not use it.
        if (ColumnHeaderText != null)
        {
            ColumnHeaderText.Text = DayModel.RepresentingDate.ToString("dddd", DayModel.CalendarRenderOptions.CalendarSettings.CultureInfo);
        }

        AllDayItemsControl.ItemsSource = DayModel.EventsCollection.AllDayEvents;

        bool isToday = DayModel.RepresentingDate.Date == DateTime.Now.Date;

        VisualStateManager.GoToState(this, isToday ? TodayState : NotTodayState, false);

        UpdateLayout();
    }
}
