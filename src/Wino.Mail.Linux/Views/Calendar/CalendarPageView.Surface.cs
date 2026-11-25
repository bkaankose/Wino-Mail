using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Calendar;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>The calendar surface in the page: bound to the ViewModel's range, settings and items; a settled period asks the ViewModel to load it.</summary>
internal sealed partial class CalendarPageView
{
    private WinoCalendarSurface? _surface;

    internal WinoCalendarSurface? Surface => _surface;

    partial void BuildSurface(Adw.Bin host)
    {
        var surface = new WinoCalendarSurface
        {
            TileFactory = (item, date) => CalendarTileMapper.Map(item, date, ViewModel.CurrentSettings),
            AccessibleNameFactory = (item, date) => CalendarTileMapper.AccessibleName(item, date, ViewModel.CurrentSettings),
            Today = () => _dateContext.GetToday()
        };
        _surface = surface;
        host.SetChild(surface.Root);
        SurfaceNavigator = surface.NavigatePeriods;

        EventHandler<CalendarPeriodEventArgs> settled = (_, args) =>
        {
            CloseQuickEvent();
            Messenger.Send(new LoadCalendarMessage(new CalendarDisplayRequest(args.DisplayType, args.AnchorDate)));
        };
        EventHandler<CalendarPeriodEventArgs> viewport = (_, args) => _toolbar.RangeText = ViewModel.FormatRangeText(args.DisplayType, args.AnchorDate);
        EventHandler today = (_, _) => RequestToday();
        _activated.Add(() =>
        {
            surface.PeriodSettled += settled;
            surface.ViewportPeriodChanged += viewport;
            surface.TodayRequested += today;
            Bind.Bind(nameof(CalendarPageViewModel.CurrentSettings), static vm => vm.CurrentSettings, settings => surface.Settings = settings);
            Bind.Bind(nameof(CalendarPageViewModel.CurrentVisibleRange), static vm => vm.CurrentVisibleRange, range => surface.VisibleRange = range);
            Bind.Bind(nameof(CalendarPageViewModel.CalendarItems), static vm => vm.CalendarItems, items => surface.Items = items is null ? (ICalendarItem[])[] : items);
            Bind.Bind(nameof(CalendarPageViewModel.IsCalendarEnabled), static vm => vm.IsCalendarEnabled, enabled => surface.Root.SetOpacity(enabled ? 1 : 0.6));
#if DEBUG
            RegisterSurfaceDebugVerbs(surface);
            RegisterPerfDebugVerbs(surface);
            RegisterRailDebugVerbs(surface);
#endif
            surface.FocusSurface();
        });
        _deactivating.Add(() =>
        {
            surface.PeriodSettled -= settled;
            surface.ViewportPeriodChanged -= viewport;
            surface.TodayRequested -= today;
            surface.Dispose();
            return Task.CompletedTask;
        });
        Bindings.Own(new Wino.Presentation.Linux.ActionDisposable(surface.Dispose));
    }
}
