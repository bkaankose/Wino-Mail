using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;
using Wino.Core.Domain.Messaging.Client.Calendar;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// Event details in their own 470pt wide window, as tall as its content (a Mac addition; Windows shows event details only in
/// the shell). The window hosts a fresh <see cref="EventDetailsPageViewController"/> bound to its
/// own EventDetailsPageViewModel. One window per event: a second request brings it forward. The
/// window closes when the event is deleted, and closing it releases the page.
/// Entry points: "Open in new window" in the details pane and in the event context menu.
/// </summary>
internal sealed class EventDetailsWindow
{
    private static readonly Dictionary<Guid, EventDetailsWindow> Open = new();
    private readonly Guid _eventId;
    private readonly EventDetailsPageViewController _page;
    private readonly NSWindow _window;
    private bool _closed;

    private EventDetailsWindow(Guid eventId, EventDetailsPageViewController page, string title)
    {
        _eventId = eventId;
        _page = page;
        _window = new NSWindow(new CGRect(0, 0, 470, 430),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered, false)
        {
            ReleasedWhenClosed = false,
            Title = title,
            ContentMinSize = new CGSize(380, 240),
            Identifier = $"event-details-{eventId:N}"
        };
        _window.ContentViewController = page;
        _window.SetContentSize(new CGSize(470, 430));
        // A short event left most of the window empty: the window follows its content's height (attendees and
        // attachments arrive after the event) until the user resizes it.
        FitToContent(page.ContentHeight);
        _window.Center();
        page.ContentHeightChanged += FitToContent;
        _window.WillStartLiveResize += (_, _) => _userSized = true;
        _window.WillClose += WindowWillClose;
        WeakReferenceMessenger.Default.Register<EventDetailsWindow, CalendarItemDeleted>(this, static (recipient, message) => recipient.EventDeleted(message));
    }

    private bool _userSized;

    private void FitToContent(double contentHeight)
    {
        if (_userSized || _closed || contentHeight <= 0) return;
        var height = Math.Clamp(contentHeight, _window.ContentMinSize.Height, 640);
        var frame = _window.Frame;
        var content = _window.ContentRectFor(frame);
        if (Math.Abs(content.Height - height) < 1) return;
        var target = _window.FrameRectFor(new CGRect(content.X, content.GetMaxY() - height, content.Width, height));
        _window.SetFrame(target, true);
    }

    private static IServiceProvider? Services => (NSApplication.SharedApplication.Delegate as AppDelegate)?.Services;

    /// <summary>Whether event details can open in their own window (PlatformCapabilities.AdditionalWindows).</summary>
    public static bool IsAvailable => Services?.GetService<IPlatformCapabilities>()?.AdditionalWindows == true;

    /// <summary>Opens <paramref name="item"/> in its own window, or brings its existing window forward.</summary>
    public static async Task OpenAsync(CalendarItem item)
    {
        if (Services is not { } services || item is null) return;
        if (!services.GetRequiredService<IPlatformCapabilities>().AdditionalWindows) return;
        var dispatcher = services.GetRequiredService<IDispatcher>();

        EventDetailsWindow? existing = null;
        await dispatcher.ExecuteOnUIThread(() => Open.TryGetValue(item.Id, out existing));
        if (existing is not null)
        {
            await dispatcher.ExecuteOnUIThread(() => existing._window.MakeKeyAndOrderFront(null));
            return;
        }

        var page = Infrastructure.PageScopes.Resolve<EventDetailsPageViewController>(services);
        page.IsInOwnWindow = true;
        try
        {
            await page.ActivateAsync(NavigationMode.New, new CalendarItemTarget(item, CalendarEventTargetType.Single));
        }
        catch
        {
            await dispatcher.ExecuteOnUIThread(page.Dispose);
            throw;
        }

        await dispatcher.ExecuteOnUIThread(() =>
        {
            var window = new EventDetailsWindow(item.Id, page, string.IsNullOrWhiteSpace(item.Title) ? Translator.MailItemNoSubject : item.Title);
            Open[item.Id] = window;
            window._window.MakeKeyAndOrderFront(null);
        });
    }

    /// <summary>Adds "Open in new window" to an event context menu (the calendar surface's right click).</summary>
    public static void AppendMenuItem(NSMenu menu, CalendarItemViewModel item)
    {
        if (!IsAvailable) return;
        var calendarItem = item.CalendarItem;
        menu.AddItem(NSMenuItem.SeparatorItem);
        var menuItem = new NSMenuItem(Translator.CalendarEventDetails_OpenInNewWindow, (_, _) => Observe(OpenAsync(calendarItem)))
        {
            Image = WinoIcons.Image(WinoIconGlyph.OpenInNewWindow, 14)
        };
        menu.AddItem(menuItem);
    }

    private void EventDeleted(CalendarItemDeleted message)
    {
        if (message.CalendarItem?.Id != _eventId) return;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => { if (!_closed) _window.Close(); });
    }

    private async void WindowWillClose(object? sender, EventArgs args)
    {
        if (_closed) return;
        _closed = true;
        _window.WillClose -= WindowWillClose;
        _page.ContentHeightChanged -= FitToContent;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        if (Open.TryGetValue(_eventId, out var owner) && ReferenceEquals(owner, this)) Open.Remove(_eventId);
        try { await _page.ReleaseAsync(); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Releasing the event details window failed."); }
        finally
        {
            _window.ClearContentViewController();
            _page.Dispose();
            _window.Dispose();
        }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Opening the event details window failed."); }
    }

#if DEBUG
    /// <summary>"event-popout N" opens the Nth event of the visible calendar range in its own window.</summary>
    public static void RegisterDebugCommands(IServiceProvider services)
    {
        MacDebugBridge.Register("event-popout", async args =>
        {
            int index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            var calendar = services.GetRequiredService<CalendarPageViewModel>();
            List<CalendarItemViewModel> visible = [];
            await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
                visible = calendar.CalendarItems.Where(item => calendar.CurrentVisibleRange?.Contains(item.StartDate) == true).OrderBy(item => item.StartDate).ToList());
            if (index < 0 || index >= visible.Count) return $"only {visible.Count} visible items (open Calendar first)";
            await OpenAsync(visible[index].CalendarItem);
            await Task.Delay(300);
            string result = string.Empty;
            await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
                result = Open.TryGetValue(visible[index].CalendarItem.Id, out var window)
                    ? $"window='{window._window.Title}' size={window._window.ContentView?.Frame.Size} open={Open.Count}"
                    : "window did not open");
            return result;
        });
    }
#endif
}
