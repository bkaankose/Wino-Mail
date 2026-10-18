using System.Collections.Specialized;
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.SwipeRefreshLayout.Widget;
using CommunityToolkit.Mvvm.Messaging;
using Google.Android.Material.AppBar;
using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Shell;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// The Calendar mode's content page (Windows CalendarPage; the iPhone and iPad CalendarPageViewController; canvas Material
/// pass boards Calendar and CalendarMonth, tablet board CalendarTablet) over the shared <see cref="CalendarPageViewModel"/>
/// and the calendar shell (<see cref="CalendarAppShellViewModel"/>: previous, next, today, the date picker, new event).
/// Compact widths show the Windows surface (Day, Week, Month) or the agenda under the range title that opens the date
/// picker sheet (option B), with the week strip in Day view; medium and expanded widths show the surface (Day, Week,
/// Work week, Month) under the period header with the event details in the shell's details pane. A tile or row tap sets
/// the shared details item, which routes the event details page. Partial files: .Agenda (compact), .Surface (the surface
/// for every width and the medium and expanded header), and <see cref="CalendarPickerSheet"/>.
/// </summary>
public sealed partial class CalendarPageFragment : WinoFragment<CalendarPageViewModel>, IShellWideContent
{
    private CalendarAppShellViewModel? _shell;
    private AndroidNavigationService? _navigation;
    private IDateContextProvider? _dateContext;
    private MaterialToolbar? _toolbar;
    private SwipeRefreshLayout? _refresh;
    private TextView? _blocked;
    private INotifyCollectionChanged? _observedItems;
    private CalendarItemViewModel? _selectedItem;
    private bool _itemsRefreshQueued;
    private bool _refreshing;

    protected override int LayoutResource => Resource.Layout.fragment_calendar;

    private IShellChrome? Chrome => ParentFragment as IShellChrome;

    private DateOnly TodayDate => _dateContext?.GetToday() ?? DateOnly.FromDateTime(DateTime.Today);

    public int DetailsPaneWidthResource(WindowWidthClass widthClass)
        => widthClass == WindowWidthClass.Expanded ? Resource.Dimension.wino_calendar_details_width_expanded : Resource.Dimension.wino_calendar_details_width_medium;

    // ---- Route hooks ----

    /// <summary>
    /// Route hook (Windows CalendarPage.OnNavigatedTo): the ViewModel's navigation, then the range load from the
    /// navigation arguments (shared <see cref="CalendarPageActivation"/>).
    /// </summary>
    public static Task InitializeAsync(CalendarPageViewModel viewModel, NavigationMode mode, object? parameter)
    {
        viewModel.OnNavigatedTo(mode, parameter);
        SendLoad(viewModel, parameter);
        return Task.CompletedTask;
    }

    /// <summary>Route hook: the calendar shell navigated to the page again (another range); the same view loads it.</summary>
    public static Task RenavigateAsync(PageEntry entry)
    {
        if (entry.ViewModel is CalendarPageViewModel viewModel) SendLoad(viewModel, entry.Parameter);
        return Task.CompletedTask;
    }

    private static void SendLoad(CalendarPageViewModel viewModel, object? parameter)
    {
        var services = MainApplication.Services;
        var today = services?.GetService<IDateContextProvider>()?.GetToday() ?? DateOnly.FromDateTime(DateTime.Today);
        bool compact = services?.GetService<IWindowSizeClassService>()?.WidthClass is null or WindowWidthClass.Compact;
        WeakReferenceMessenger.Default.Send(CalendarPageActivation.CreateLoadMessage(parameter,
            DisplayTypeFor(viewModel.StatePersistanceService.CalendarDisplayType, compact), today));
    }

    /// <summary>Phones offer Day, Week and Month (date picker option B); Work week opens as Week there.</summary>
    private static CalendarDisplayType DisplayTypeFor(CalendarDisplayType persisted, bool compact)
        => compact && persisted == CalendarDisplayType.WorkWeek ? CalendarDisplayType.Week : persisted;

    // ---- View ----

    protected override void OnBind(View view, CalendarPageViewModel viewModel, IDispatcher dispatcher)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        var context = view.RequireContext();
        _shell = services.GetRequiredService<CalendarAppShellViewModel>();
        _navigation = services.GetService<AndroidNavigationService>();
        _dateContext = services.GetService<IDateContextProvider>();
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.calendar_toolbar);
        _blocked = view.FindViewById<TextView>(Resource.Id.calendar_blocked);
        _refresh = view.FindViewById<SwipeRefreshLayout>(Resource.Id.calendar_refresh);

        // The docked search bar every mode shows (menu, "Search calendar", the page's actions).
        BuildSearch(context);

        if (_refresh is not null)
        {
            // Pull to refresh is the only way to synchronize (no Sync button): the calendar shell's synchronization.
            _refresh.SetColorSchemeColors(WinoColors.Accent(context));
            _refresh.SetProgressBackgroundColorSchemeColor(WinoColors.ZoneFill(context));
            EventHandler refreshed = async (_, _) =>
            {
                if (_refreshing) return;
                _refreshing = true;
                try { if (_navigation?.Shell is { } shell) await shell.SynchronizeAsync(); }
                finally
                {
                    _refreshing = false;
                    if (_refresh is not null) _refresh.Refreshing = false;
                }
            };
            _refresh.Refresh += refreshed;
            Bindings.Own(new ActionDisposable(() => _refresh.Refresh -= refreshed));
        }

        // The compact layout has the range title (date picker option B); wider layouts the period header.
        if (view.FindViewById(Resource.Id.calendar_title) is not null) BuildPhone(view, context, viewModel);
        else BuildSurface(view, context, viewModel);

        // A details item left over from a details page that closed while this view was away (compact widths) is stale.
        if (viewModel.DisplayDetailsCalendarItemViewModel is not null && _navigation?.RenderingEntry is null)
            viewModel.DisplayDetailsCalendarItemViewModel = null;

        Bind(viewModel, dispatcher, nameof(viewModel.CurrentVisibleRange), vm => vm.CurrentVisibleRange, RangeChanged);
        Bind(viewModel, dispatcher, nameof(viewModel.CurrentSettings), vm => vm.CurrentSettings, SettingsChanged);
        Bind(viewModel, dispatcher, nameof(viewModel.CalendarItems), vm => vm.CalendarItems, ObserveItems);
        Bind(viewModel, dispatcher, nameof(viewModel.VisibleDateRangeText), vm => vm.VisibleDateRangeText, RangeTextChanged);
        Bind(viewModel, dispatcher, nameof(viewModel.DisplayDetailsCalendarItemViewModel), vm => vm.DisplayDetailsCalendarItemViewModel, DetailsItemChanged);
        Bindings.Own(new PropertyBinding<IStatePersistanceService, CalendarDisplayType>(viewModel.StatePersistanceService,
            nameof(IStatePersistanceService.CalendarDisplayType), state => state.CalendarDisplayType, DisplayTypeChanged, dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, bool>(viewModel.Readiness, nameof(ModeReadinessViewModel.IsBlocked),
            readiness => readiness.IsBlocked, _ => ApplyReadiness(), dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ModeReadinessViewModel, string>(viewModel.Readiness, nameof(ModeReadinessViewModel.Message),
            readiness => readiness.Message, _ => ApplyReadiness(), dispatcher, ReportError));

        if (_navigation is not null)
        {
            _navigation.ShellPagesChanged += ShellPagesChanged;
            Bindings.Own(new ActionDisposable(() => _navigation.ShellPagesChanged -= ShellPagesChanged));
        }
        Bindings.Own(new ActionDisposable(() =>
        {
            ObserveItems(null);
            if (_selectedItem is not null) _selectedItem.IsSelected = false;
            _selectedItem = null;
        }));
    }

    private void RangeChanged(VisibleDateRange? range)
    {
        SurfaceRangeChanged(range);
        PhoneRangeChanged(range);
    }

    private void SettingsChanged(CalendarSettings? settings)
    {
        SurfaceSettingsChanged(settings);
        PhoneSettingsChanged(settings);
    }

    private void RangeTextChanged(string? text)
    {
        if (IsPhone) PhoneRangeTextChanged(text);
        else SurfaceRangeTextChanged(text);
    }

    private void DisplayTypeChanged(CalendarDisplayType type)
    {
        SurfaceDisplayTypeChanged(type);
        PhoneDisplayTypeChanged(type);
    }

    private void ObserveItems(IEnumerable<CalendarItemViewModel>? items)
    {
        if (_observedItems is not null) _observedItems.CollectionChanged -= ItemsCollectionChanged;
        _observedItems = items as INotifyCollectionChanged;
        if (_observedItems is not null) _observedItems.CollectionChanged += ItemsCollectionChanged;
        if (items is not null) ItemsChanged();
    }

    private void ItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_itemsRefreshQueued) return;
        _itemsRefreshQueued = true;
        View?.Post(() =>
        {
            _itemsRefreshQueued = false;
            if (View is not null) ItemsChanged();
        });
    }

    private void ItemsChanged()
    {
        SurfaceItemsChanged();
        PhoneItemsChanged();
    }

    private void ApplyReadiness()
    {
        if (ViewModel is not { } viewModel || _blocked is null) return;
        var readiness = viewModel.Readiness;
        _blocked.Visibility = readiness.IsBlocked ? ViewStates.Visible : ViewStates.Gone;
        _blocked.Text = readiness.IsBlocked ? $"{readiness.Title}\n{readiness.Message}" : string.Empty;
        if (_refresh is not null) _refresh.Visibility = readiness.IsBlocked ? ViewStates.Invisible : ViewStates.Visible;
        SurfaceReadinessChanged(readiness.IsBlocked);
    }

    // ---- Selection and the details route ----

    /// <summary>A tile or agenda row was tapped: the shared message sets the details item, which routes the details page.</summary>
    private void SelectItem(CalendarItemViewModel item)
    {
        if (ViewModel is not { } viewModel) return;
        // The item whose details just closed must route again.
        if (ReferenceEquals(viewModel.DisplayDetailsCalendarItemViewModel, item) && _navigation?.RenderingEntry is null)
            viewModel.DisplayDetailsCalendarItemViewModel = null;
        WeakReferenceMessenger.Default.Send(new CalendarItemTappedMessage(item));
    }

    private void DetailsItemChanged(CalendarItemViewModel? item)
    {
        if (!ReferenceEquals(_selectedItem, item))
        {
            if (_selectedItem is not null) _selectedItem.IsSelected = false;
            _selectedItem = item;
            if (item is not null) item.IsSelected = true;
            SelectionChanged();
        }
        if (item is not null && ViewModel is { } viewModel && !IsShowingDetailsOf(item)) viewModel.NavigateEventDetailsCommand.Execute(null);
    }

    private bool IsShowingDetailsOf(CalendarItemViewModel item)
        => _navigation?.RenderingEntry is { Page: WinoPage.EventDetailsPage, Parameter: CalendarItemTarget target } && target.Item.Id == item.Id;

    /// <summary>The details page closed (back, Close, or a range change released it): the selection goes with it.</summary>
    private void ShellPagesChanged(object? sender, EventArgs args)
    {
        if (ViewModel is { DisplayDetailsCalendarItemViewModel: not null } viewModel && _navigation?.RenderingEntry is null)
            viewModel.DisplayDetailsCalendarItemViewModel = null;
    }

    private void SelectionChanged()
    {
        SurfaceSelectionChanged();
        PhoneSelectionChanged();
    }

    private void GoPrevious() => _shell?.PreviousDateRangeCommand.Execute(null);
    private void GoNext() => _shell?.NextDateRangeCommand.Execute(null);
    private void GoToday() => _shell?.TodayClickedCommand.Execute(null);
    private void NewEvent() => _shell?.NewEventCommand.Execute(null);

    /// <summary>Asks the shared state for another display type; the calendar shell loads the new range.</summary>
    private void RequestDisplayType(CalendarDisplayType type)
    {
        if (ViewModel is { } viewModel && viewModel.StatePersistanceService.CalendarDisplayType != type)
            viewModel.StatePersistanceService.CalendarDisplayType = type;
    }
}
