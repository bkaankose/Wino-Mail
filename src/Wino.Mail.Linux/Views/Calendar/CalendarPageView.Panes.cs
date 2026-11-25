using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The page as rendering frame: event details go to the details zone, any other page covers the whole page. One gate
/// serialises show and clear; the outgoing view is released before its widget leaves.
/// </summary>
internal sealed partial class CalendarPageView
{
    private readonly SemaphoreSlim _paneGate = new(1, 1);
    private IWinoView? _details;
    private IWinoView? _cover;
    private Gtk.Widget? _coverZone;
    private EventHandler? _coverClose;
    private bool _detailsFloating;

    public object? CurrentViewModel => _details?.AssociatedViewModel ?? _cover?.AssociatedViewModel;

    public async Task ShowAsync(WinoPage page, object? parameter, Func<IWinoView> create)
    {
        await _paneGate.WaitAsync();
        try
        {
            if (IsReleased) return;
            var view = create();
            if (IsReleased)
            {
                await view.ReleaseAsync();
                return;
            }
            if (page == WinoPage.EventDetailsPage) await ReplaceDetailsAsync(view);
            else await ReplaceCoverAsync(view);
            if (IsReleased) return;
            await view.ActivateAsync(NavigationMode.New, parameter);
        }
        finally { _paneGate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _paneGate.WaitAsync();
        try
        {
            await ReplaceDetailsAsync(null);
            await ReplaceCoverAsync(null);
        }
        finally { _paneGate.Release(); }
    }

    public void SetCover(Gtk.Widget? cover)
    {
    }

    public bool Detach(IWinoView view) => false;

    private async Task ReplaceDetailsAsync(IWinoView? next)
    {
        var previous = _details;
        _details = null;
        if (previous is EventDetailsView poppedOut) poppedOut.PoppedOut -= DetailsPoppedOut;
        if (previous is not null)
        {
            try { await previous.ReleaseAsync(); }
            catch (Exception exception) { ReportError(exception); }
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _detailsZone.SetChild(null);
            var show = next is not null && !IsReleased;
            _details = show ? next : null;
            if (show && next is not null) _detailsZone.SetChild(next.Root);
            if (show && next is EventDetailsView details) details.PoppedOut += DetailsPoppedOut;
            _detailsZone.SetVisible(show);
        });
        if (next is not null && _details is null) await next.ReleaseAsync();
    }

    private async Task ReplaceCoverAsync(IWinoView? next)
    {
        var previous = _cover;
        _cover = null;
        if (previous is ICalendarCoverView closing && _coverClose is { } handler) closing.CloseRequested -= handler;
        _coverClose = null;
        if (previous is not null)
        {
            try { await previous.ReleaseAsync(); }
            catch (Exception exception) { ReportError(exception); }
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_coverZone is { } zone)
            {
                _root.RemoveOverlay(zone);
                _coverZone = null;
            }
            if (next is null || IsReleased) return;
            var cover = WinoZone.Create(next.Root, WinoThemeSurface.Workspace);
            cover.AddCssClass("view");
            cover.SetHalign(Gtk.Align.Fill);
            cover.SetValign(Gtk.Align.Fill);
            _root.AddOverlay(cover);
            _coverZone = cover;
            _cover = next;
            if (next is ICalendarCoverView coverView)
            {
                _coverClose = (_, _) => Observe(DismissCoverAsync());
                coverView.CloseRequested += _coverClose;
            }
        });
        if (next is not null && _cover is null) await next.ReleaseAsync();
    }

    private void DetailsPoppedOut(object? sender, EventArgs args) => ViewModel.DisplayDetailsCalendarItemViewModel = null;

    private async Task DismissCoverAsync()
    {
        await _paneGate.WaitAsync();
        try { await ReplaceCoverAsync(null); }
        finally { _paneGate.Release(); }
    }

    private void SetDetailsFloating(bool floating)
    {
        if (_detailsFloating == floating) return;
        _detailsFloating = floating;
        if (floating)
        {
            _columns.Remove(_detailsZone);
            _detailsZone.SetHalign(Gtk.Align.End);
            _detailsZone.AddCssClass(WinoCssClass.CalendarDetailsFloating);
            _body.AddOverlay(_detailsZone);
        }
        else
        {
            _body.RemoveOverlay(_detailsZone);
            _detailsZone.SetHalign(Gtk.Align.Fill);
            _detailsZone.RemoveCssClass(WinoCssClass.CalendarDetailsFloating);
            _columns.Append(_detailsZone);
        }
    }

#if DEBUG
    internal string DescribePanes()
        => $"details={_details?.GetType().Name ?? "-"} cover={_cover?.GetType().Name ?? "-"} floating={_detailsFloating} compact={_toolbar.IsCompact}";
#endif
}
