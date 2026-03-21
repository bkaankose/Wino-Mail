using System;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Navigation;
using Wino.Calendar.Views.Abstract;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views;

public sealed partial class CalendarPage : CalendarPageAbstract
{
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
}
