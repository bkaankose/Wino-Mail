using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Wino.Core.Domain;
using Wino.Calendar.Controls;
using Wino.Calendar.Views.Abstract;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.Views;

public sealed partial class CalendarPage : CalendarPageAbstract, ITitleBarSearchHost
{
    private const int PopupDialogOffset = 12;
    private ICalendarShellClient CalendarShellClient { get; } = WinoApplication.Current.Services.GetRequiredService<ICalendarShellClient>();
    private CancellationTokenSource? _searchCancellationTokenSource;
    private long _calendarTypeSelectorChangedToken;

    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];

    public string SearchText { get; set; } = string.Empty;

    public string SearchPlaceholderText => Translator.SearchBarPlaceholder;

    public CalendarPage()
    {
        InitializeComponent();
        _calendarTypeSelectorChangedToken = CalendarToolbar.RegisterSelectedTypeChanged(CalendarTypeSelectorSelectedTypeChanged);
        CalendarToolbar.PreviousDateRequested += CalendarToolbarPreviousDateRequested;
        CalendarToolbar.NextDateRequested += CalendarToolbarNextDateRequested;
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
        CalendarShellClient.PropertyChanged += CalendarShellClientPropertyChanged;
        CalendarShellClient.StatePersistenceService.StatePropertyChanged += CalendarStatePersistenceServiceChanged;
        Unloaded += CalendarPageUnloaded;
        RefreshCalendarToolbar();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshCalendarToolbar();

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

    public async Task OnTitleBarSearchTextChangedAsync()
    {
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _searchCancellationTokenSource = null;

        SearchSuggestions.Clear();

        if (string.IsNullOrWhiteSpace(SearchText))
            return;

        _searchCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _searchCancellationTokenSource.Token;

        try
        {
            await Task.Delay(150, cancellationToken);
            var results = await ViewModel.SearchCalendarItemsAsync(SearchText, 6, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            foreach (var result in results)
            {
                var subtitleParts = new[]
                {
                    result.AssignedCalendar?.MailAccount?.Name,
                    result.AssignedCalendar?.Name,
                    result.LocalStartDate.ToString("g")
                }.Where(part => !string.IsNullOrWhiteSpace(part));

                SearchSuggestions.Add(new TitleBarSearchSuggestion(result.Title, string.Join(" • ", subtitleParts), result));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion)
    {
        SearchText = suggestion.Title;
    }

    public async Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion)
    {
        SearchText = queryText;

        if (chosenSuggestion?.Tag is CalendarItem selectedItem)
        {
            ViewModel.OpenCalendarSearchResult(selectedItem);
            return;
        }

        var result = (await ViewModel.SearchCalendarItemsAsync(queryText, 1, CancellationToken.None)).FirstOrDefault();
        if (result != null)
        {
            ViewModel.OpenCalendarSearchResult(result);
        }
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

    private void CalendarTypeSelectorSelectedTypeChanged(DependencyObject sender, DependencyProperty dp)
    {
        var selectedType = CalendarToolbar.SelectedType;
        if (CalendarShellClient.StatePersistenceService.CalendarDisplayType != selectedType)
        {
            CalendarShellClient.StatePersistenceService.CalendarDisplayType = selectedType;
        }
    }

    private void CalendarToolbarPreviousDateRequested(object? sender, EventArgs e)
        => CalendarShellClient.PreviousDateRangeCommand.Execute(null);

    private void CalendarToolbarNextDateRequested(object? sender, EventArgs e)
        => CalendarShellClient.NextDateRangeCommand.Execute(null);

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.VisibleDateRangeText))
        {
            RefreshCalendarToolbar();
        }
    }

    private void CalendarShellClientPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ICalendarShellClient.VisibleDateRangeText))
        {
            RefreshCalendarToolbar();
        }
    }

    private void CalendarStatePersistenceServiceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IStatePersistanceService.CalendarDisplayType) ||
            propertyName == nameof(IStatePersistanceService.DayDisplayCount))
        {
            RefreshCalendarToolbar();
        }
    }

    private void RefreshCalendarToolbar()
    {
        CalendarToolbar.VisibleDateRangeText = CalendarShellClient.VisibleDateRangeText;
        CalendarToolbar.TodayClickedCommand = CalendarShellClient.TodayClickedCommand;
        CalendarToolbar.DisplayDayCount = CalendarShellClient.StatePersistenceService.DayDisplayCount;

        if (CalendarToolbar.SelectedType != CalendarShellClient.StatePersistenceService.CalendarDisplayType)
        {
            CalendarToolbar.SelectedType = CalendarShellClient.StatePersistenceService.CalendarDisplayType;
        }
    }

    private void CalendarPageUnloaded(object sender, RoutedEventArgs e)
    {
        CalendarToolbar.UnregisterSelectedTypeChanged(_calendarTypeSelectorChangedToken);
        CalendarToolbar.PreviousDateRequested -= CalendarToolbarPreviousDateRequested;
        CalendarToolbar.NextDateRequested -= CalendarToolbarNextDateRequested;
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        CalendarShellClient.PropertyChanged -= CalendarShellClientPropertyChanged;
        CalendarShellClient.StatePersistenceService.StatePropertyChanged -= CalendarStatePersistenceServiceChanged;
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        Unloaded -= CalendarPageUnloaded;
    }
}
