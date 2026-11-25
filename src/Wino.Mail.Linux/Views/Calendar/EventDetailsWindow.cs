using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Calendar;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Event details in a window of their own (macOS EventDetailsWindow) on the shared window host, one per event under
/// the key <c>event:&lt;id&gt;</c>: a second request presents the existing window. The window closes when its event is
/// deleted. Unlike the Mac it keeps its size instead of following the content height.
/// </summary>
internal static class EventDetailsWindow
{
    public const string Kind = "event";
    private const int DefaultWidth = 470;
    private const int DefaultHeight = 430;

    public static bool IsAvailable(IPlatformCapabilities capabilities) => capabilities.AdditionalWindows;

    public static string Key(Guid itemId) => $"event:{itemId}";

    public static async Task OpenAsync(IServiceProvider services, CalendarItem item)
    {
        var windows = services.GetRequiredService<WindowManager>();
        var dispatcher = services.GetRequiredService<IDispatcher>();
        var key = Key(item.Id);
        var presented = false;
        await dispatcher.ExecuteOnUIThread(() => presented = windows.TryPresent(key));
        if (presented) return;

        EventDetailsView? view = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            view = PageScopes.Create<EventDetailsView>(services);
            view.IsInOwnWindow = true;
        });
        if (view is null) return;
        try { await view.ActivateAsync(NavigationMode.New, new CalendarItemTarget(item, CalendarEventTargetType.Single)); }
        catch
        {
            await view.ReleaseAsync();
            throw;
        }

        var opened = false;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (Gio.Application.GetDefault() is not Gtk.Application application || windows.Find(key) is not null) return;
            var title = string.IsNullOrWhiteSpace(item.Title) ? Translator.MailItemNoSubject : item.Title;
            var window = new SecondaryWindow(application, Kind, view.Root, title, DefaultWidth, DefaultHeight, windows.Sizes, 380, 240);
            var deleted = new DeletionWatch(window, item.Id, dispatcher);
            window.Closing = async () =>
            {
                deleted.Dispose();
                await view.ReleaseAsync();
            };
            windows.Add(key, window);
            view.OwnWindow = window.Window;
#if DEBUG
            DebugViews[key] = view;
            window.Own(new Wino.Presentation.Linux.ActionDisposable(() => DebugViews.Remove(key)));
#endif
            window.Present();
            opened = true;
        });
        if (!opened) await view.ReleaseAsync();
    }

#if DEBUG
    private static readonly Dictionary<string, EventDetailsView> DebugViews = [];

    public static EventDetailsView? DebugFind(string key) => DebugViews.GetValueOrDefault(key);

    public static string[] DebugDescribe() => [.. DebugViews.Select(static entry => $"{entry.Key}\t{entry.Value.Describe()}")];
#endif

    /// <summary>Closes the window when the shown event is deleted.</summary>
    internal sealed class DeletionWatch : IRecipient<CalendarItemDeleted>, IDisposable
    {
        private readonly SecondaryWindow _window;
        private readonly Guid _itemId;
        private readonly IDispatcher _dispatcher;

        public DeletionWatch(SecondaryWindow window, Guid itemId, IDispatcher dispatcher)
        {
            _window = window;
            _itemId = itemId;
            _dispatcher = dispatcher;
            WeakReferenceMessenger.Default.Register(this);
            window.Own(this);
        }

        public void Receive(CalendarItemDeleted message)
        {
            if (message.CalendarItem.Id != _itemId) return;
            _ = _dispatcher.ExecuteOnUIThread(() =>
            {
                if (!_window.IsClosing) _window.Close();
            });
        }

        public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
