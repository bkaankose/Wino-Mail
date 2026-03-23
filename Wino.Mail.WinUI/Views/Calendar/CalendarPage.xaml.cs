using System;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Wino.Calendar.Controls;
using Wino.Calendar.Views.Abstract;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views;

public sealed partial class CalendarPage : CalendarPageAbstract
{
    private const int PopupDialogOffset = 12;

    public CalendarPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.NavigationMode == NavigationMode.Back && ViewModel.RestoreVisibleState())
        {
            return;
        }

        var anchorDate = DateOnly.FromDateTime(DateTime.Now.Date);

        if (e.Parameter is CalendarPageNavigationArgs args && !args.RequestDefaultNavigation)
        {
            anchorDate = DateOnly.FromDateTime(args.NavigationDate.Date);
        }

        var request = new CalendarDisplayRequest(ViewModel.StatePersistanceService.CalendarDisplayType, anchorDate);
        WeakReferenceMessenger.Default.Send(new LoadCalendarMessage(request));
    }

    private void CalendarSurfaceEmptySlotTapped(object sender, CalendarEmptySlotTappedEventArgs e)
    {
        if (ViewModel.DisplayDetailsCalendarItemViewModel != null)
        {
            ViewModel.DisplayDetailsCalendarItemViewModel = null;
            return;
        }

        ViewModel.SelectedQuickEventDate = e.ClickedDate;

        var transform = CalendarSurface.TransformToVisual(CalendarOverlayCanvas);
        var canvasPoint = transform.TransformPoint(e.PositionerPoint);

        TeachingTipPositionerGrid.Width = e.CellSize.Width;
        TeachingTipPositionerGrid.Height = e.CellSize.Height;

        Canvas.SetLeft(TeachingTipPositionerGrid, canvasPoint.X);
        Canvas.SetTop(TeachingTipPositionerGrid, canvasPoint.Y);

        var startTime = e.ClickedDate.TimeOfDay;
        var endTime = startTime.Add(TimeSpan.FromMinutes(30));
        ViewModel.SelectQuickEventTimeRange(startTime, endTime);

        QuickEventPopupDialog.IsOpen = true;
    }

    private void QuickEventAccountSelectorSelectionChanged(object sender, SelectionChangedEventArgs e)
        => QuickEventAccountSelectorFlyout.Hide();

    private void QuickEventPopupClosed(object sender, object e)
    {
    }

    private void PopupPlacementChanged(object sender, object e)
    {
        if (sender is not Popup popup)
        {
            return;
        }

        popup.HorizontalOffset = 0;
        popup.VerticalOffset = 0;

        switch (popup.ActualPlacement)
        {
            case PopupPlacementMode.Top:
                popup.VerticalOffset = PopupDialogOffset * -1;
                break;
            case PopupPlacementMode.Bottom:
                popup.VerticalOffset = PopupDialogOffset;
                break;
            case PopupPlacementMode.Left:
                popup.HorizontalOffset = PopupDialogOffset * -1;
                break;
            case PopupPlacementMode.Right:
                popup.HorizontalOffset = PopupDialogOffset;
                break;
        }
    }

    private void StartTimeDurationSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
        => ViewModel.SelectedStartTimeString = args.Text;

    private void EndTimeDurationSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
        => ViewModel.SelectedEndTimeString = args.Text;
}
