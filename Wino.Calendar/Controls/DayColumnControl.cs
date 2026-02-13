using System;
using System.Collections.Specialized;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Collections;
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
    private CalendarEventCollection _boundEventsCollection;

    public CalendarDayModel DayModel
    {
        get { return (CalendarDayModel)GetValue(DayModelProperty); }
        set { SetValue(DayModelProperty, value); }
    }

    public static readonly DependencyProperty DayModelProperty = DependencyProperty.Register(
        nameof(DayModel),
        typeof(CalendarDayModel),
        typeof(DayColumnControl),
        new PropertyMetadata(null, new PropertyChangedCallback(OnRenderingPropertiesChanged)));

    public DayColumnControl()
    {
        DefaultStyleKey = typeof(DayColumnControl);
        Unloaded += OnUnloaded;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        HeaderDateDayText = GetTemplateChild(PART_HeaderDateDayText) as TextBlock;
        ColumnHeaderText = GetTemplateChild(PART_ColumnHeaderText) as TextBlock;
        IsTodayBorder = GetTemplateChild(PART_IsTodayBorder) as Border;
        AllDayItemsControl = GetTemplateChild(PART_AllDayItemsControl) as ItemsControl;

        RegisterEventsCollectionHandlers();
        UpdateValues();
    }

    private static void OnRenderingPropertiesChanged(DependencyObject control, DependencyPropertyChangedEventArgs e)
    {
        if (control is DayColumnControl columnControl)
        {
            columnControl.RegisterEventsCollectionHandlers();
            columnControl.UpdateValues();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DeregisterEventsCollectionHandlers();
    }

    private bool IsMonthlyTemplate() => ColumnHeaderText == null;

    private void RegisterEventsCollectionHandlers()
    {
        var nextCollection = DayModel?.EventsCollection;
        if (ReferenceEquals(_boundEventsCollection, nextCollection))
            return;

        DeregisterEventsCollectionHandlers();

        _boundEventsCollection = nextCollection;
        if (_boundEventsCollection == null)
            return;

        ((INotifyCollectionChanged)_boundEventsCollection.AllDayEvents).CollectionChanged += EventsCollectionChanged;
        ((INotifyCollectionChanged)_boundEventsCollection.RegularEvents).CollectionChanged += EventsCollectionChanged;
    }

    private void DeregisterEventsCollectionHandlers()
    {
        if (_boundEventsCollection == null)
            return;

        ((INotifyCollectionChanged)_boundEventsCollection.AllDayEvents).CollectionChanged -= EventsCollectionChanged;
        ((INotifyCollectionChanged)_boundEventsCollection.RegularEvents).CollectionChanged -= EventsCollectionChanged;
        _boundEventsCollection = null;
    }

    private void EventsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEventItemsSource();
    }

    private void UpdateEventItemsSource()
    {
        if (AllDayItemsControl == null || DayModel == null) return;

        if (IsMonthlyTemplate())
        {
            // Month cells should show all events for the day, not only all-day/multi-day.
            var monthlyItems = DayModel.EventsCollection.AllDayEvents
                .Concat(DayModel.EventsCollection.RegularEvents)
                .GroupBy(a => a.Id)
                .Select(g => g.First())
                .OrderBy(a => a.StartDate)
                .ToList();

            AllDayItemsControl.ItemsSource = monthlyItems;
            return;
        }

        AllDayItemsControl.ItemsSource = DayModel.EventsCollection.AllDayEvents;
    }

    private void UpdateValues()
    {
        if (DayModel == null) return;

        if (HeaderDateDayText != null)
        {
            HeaderDateDayText.Text = DayModel.RepresentingDate.Day.ToString();
        }

        // Monthly template does not use it.
        if (ColumnHeaderText != null)
        {
            ColumnHeaderText.Text = DayModel.RepresentingDate.ToString("dddd", DayModel.CalendarRenderOptions.CalendarSettings.CultureInfo);
        }

        UpdateEventItemsSource();

        if (IsTodayBorder == null) return;
        bool isToday = DayModel.RepresentingDate.Date == DateTime.Now.Date;

        VisualStateManager.GoToState(this, isToday ? TodayState : NotTodayState, false);

        UpdateLayout();
    }
}
