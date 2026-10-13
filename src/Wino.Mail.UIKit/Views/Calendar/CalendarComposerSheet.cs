using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The event composer as a full-screen sheet (iPhone). The calendar page takes the composer as the content page's
/// rendering frame (<see cref="IRenderingFrameHost"/>) and presents it here: Cancel and a created event close it through
/// <see cref="CalendarEventComposePageViewController.CloseRequested"/>, a swipe or the page leaving close it too, and the
/// composer is then released by its host rather than through the router's Settings sheet bookkeeping.
/// </summary>
public sealed class CalendarComposerSheet
{
    private readonly IUIKitShellHost _host;
    private readonly IServiceProvider _services;
    private readonly CalendarEventComposePageViewController _composer;
    private UINavigationController? _sheet;
    private bool _closed;

    private CalendarComposerSheet(IUIKitShellHost host, IServiceProvider services, CalendarEventComposePageViewController composer)
    {
        _host = host;
        _services = services;
        _composer = composer;
    }

    /// <summary>The sheet on screen, if any (the page leaving, debug).</summary>
    public static CalendarComposerSheet? Current { get; private set; }

    public static bool Present(IUIKitShellHost host, IServiceProvider services, CalendarEventComposePageViewController composer)
    {
        if (host.TopViewController is null) return false;
        var session = new CalendarComposerSheet(host, services, composer);
        composer.CloseRequested += session.CloseRequested;
        session._sheet = host.PresentSheet(composer, UIKitPageHost.FullScreenSheet, session.DismissedByUser);
        Current = session;
        return true;
    }

    /// <summary>Closes the sheet as Cancel would (debug bridge, or the calendar page leaving).</summary>
    public Task CloseAsync() => CloseCoreAsync(dismiss: true);

    private async void CloseRequested(object? sender, EventArgs args)
    {
        try { await CloseCoreAsync(dismiss: true); }
        catch (Exception exception) { _services.GetService<IWinoLogger>()?.CaptureException(exception, nameof(CalendarComposerSheet)); }
    }

    private async void DismissedByUser()
    {
        try { await CloseCoreAsync(dismiss: false); }
        catch (Exception exception) { _services.GetService<IWinoLogger>()?.CaptureException(exception, nameof(CalendarComposerSheet)); }
    }

    private async Task CloseCoreAsync(bool dismiss)
    {
        if (_closed) return;
        _closed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        _composer.CloseRequested -= CloseRequested;
        if (dismiss && _sheet is { } sheet) await _host.DismissSheetAsync(sheet);
        await ReleaseAsync(_services, _composer);
    }

    /// <summary>Releases a composer its calendar host closed (the sheet, or the iPad page in place).</summary>
    public static async Task ReleaseAsync(IServiceProvider services, UIViewController composer)
    {
        try { if (composer is IWinoViewController routed) await routed.ReleaseAsync(); }
        catch (Exception exception) { services.GetService<IWinoLogger>()?.CaptureException(exception, nameof(CalendarComposerSheet)); }
    }
}
