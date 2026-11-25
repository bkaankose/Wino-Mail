using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The quick event (macOS CalendarPageViewController quick event part): a slot click or a dragged range begins it in the
/// ViewModel and opens <see cref="QuickEventPopover"/> beside the range; while it is open the surface highlights the
/// ViewModel's range, so time changes in the popover move the highlight.
/// </summary>
internal sealed partial class CalendarPageView
{
    private const int QuickEventWidth = 340;

    private QuickEventPopover? _quickEvent;

    partial void BuildQuickEvent()
    {
        if (_surface is not { } surface) return;
        _activated.Add(() =>
        {
            Bind.Bind(nameof(CalendarPageViewModel.SelectedQuickEventDate), static vm => vm.SelectedQuickEventDate, _ => ApplySelectionRange());
            Bind.Bind(nameof(CalendarPageViewModel.QuickEventSelectionEnd), static vm => vm.QuickEventSelectionEnd, _ => ApplySelectionRange());
            Bind.Bind(nameof(CalendarPageViewModel.SelectedStartTimeString), static vm => vm.SelectedStartTimeString, _ => ApplySelectionRange());
            Bind.Bind(nameof(CalendarPageViewModel.IsAllDay), static vm => vm.IsAllDay, _ => ApplySelectionRange());
#if DEBUG
            RegisterQuickEventDebugVerbs(surface);
#endif
        });
        _deactivating.Add(() =>
        {
            _quickEvent?.Dispose();
            _quickEvent = null;
            return Task.CompletedTask;
        });
    }

    partial void OpenQuickEvent(CalendarSlotEventArgs args)
    {
        if (IsReleased || _surface is not { } surface || !ViewModel.Readiness.IsReady) return;
        var month = ViewModel.CurrentVisibleRange?.DisplayType == CalendarDisplayType.Month;
        var isAllDay = args.IsAllDay || month;
        _quickEvent?.Dismiss();
        _quickEvent = null;
        ViewModel.BeginQuickEvent(args.Start, args.End, isAllDay);
        var popover = new QuickEventPopover(ViewModel, Dispatcher, ReportError);
        popover.Closed += (sender, _) =>
        {
            if (!ReferenceEquals(_quickEvent, sender)) return;
            _quickEvent = null;
            surface.SelectionRange = null;
        };
        _quickEvent = popover;
        var side = Gtk.PositionType.Right;
        if (isAllDay) side = Gtk.PositionType.Bottom;
        else if (args.Anchor.X + args.Anchor.Width + QuickEventWidth > args.AnchorWidget.GetWidth()) side = Gtk.PositionType.Left;
        popover.Present(args.AnchorWidget, args.Anchor, side);
    }

    partial void CloseQuickEvent()
    {
        if (_quickEvent is null) return;
        _quickEvent.Dismiss();
        _quickEvent = null;
        ViewModel.SelectedQuickEventDate = null;
        if (_surface is { } surface) surface.SelectionRange = null;
    }

    /// <summary>The ViewModel's quick event range as the surface highlight (macOS ApplySelectionRange).</summary>
    private void ApplySelectionRange()
    {
        if (_surface is not { } surface) return;
        CalendarSelectionRange? range = null;
        var month = ViewModel.CurrentVisibleRange?.DisplayType == CalendarDisplayType.Month;
        if (ViewModel.SelectedQuickEventDate is { } date && ViewModel.QuickEventSelectionEnd is { } end && (month || !ViewModel.IsAllDay))
        {
            var start = ViewModel.IsAllDay ? date.Date
                : ViewModel.CurrentSettings?.GetTimeSpan(ViewModel.SelectedStartTimeString) is { } time ? date.Date.Add(time) : date;
            if (end > start) range = new CalendarSelectionRange(start, end);
        }
        surface.SelectionRange = range;
    }
}
