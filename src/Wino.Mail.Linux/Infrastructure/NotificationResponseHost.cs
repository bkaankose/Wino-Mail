using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux.Notifications;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Notification clicks (Apple AppleNotificationResponseHandler, Windows toast activation): the application action every
/// notification targets, decoded into a <see cref="NotificationResponse"/> for the shared response rules. A click that
/// needs the window waits while the app is locked; the body brings the window forward. It also runs the calendar and
/// task reminder pollers while the app runs.
/// </summary>
internal sealed class NotificationResponseHost(IServiceProvider services, IDispatcher dispatcher) : IApplicationLifecycleHook
{
    private readonly List<NotificationResponse> _waiting = [];
    private Gio.SimpleAction? _action;
    private AppLockCoordinator? _lock;

    public async Task ServicesReadyAsync()
    {
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (Gio.Application.GetDefault() is not { } application) return;
            var action = _action = Gio.SimpleAction.New(LinuxNotificationActions.ActionName, GLib.VariantType.New("as"));
            action.OnActivate += (_, args) => Receive(args.Parameter);
            application.AddAction(action);
            _lock = services.GetService<AppLockCoordinator>();
            if (_lock is not null) _lock.Unlocked += Flush;
        });
        try { await services.GetRequiredService<ICalendarReminderServer>().StartAsync(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "The calendar reminder poller could not start."); }
        try { await services.GetRequiredService<ITaskReminderServer>().StartAsync(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "The task reminder poller could not start."); }
    }

    public async Task StoppingAsync(bool runtimeStarted)
    {
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (_lock is not null) _lock.Unlocked -= Flush;
            if (_action is not null) Gio.Application.GetDefault()?.RemoveAction(LinuxNotificationActions.ActionName);
            _action = null;
            _waiting.Clear();
        });
        if (!runtimeStarted) return;
        await services.GetRequiredService<ICalendarReminderServer>().StopAsync();
        await services.GetRequiredService<ITaskReminderServer>().StopAsync();
    }

    /// <summary>The same path a notification server's click takes (main thread).</summary>
    internal void Receive(GLib.Variant? parameter)
    {
        if (LinuxNotificationActions.Decode(parameter) is not { } response)
        {
            Serilog.Log.Warning("A notification response could not be read.");
            return;
        }
        Serilog.Log.Information("Notification response {Kind} {Action} ({Category}).", response.Kind, response.ActionIdentifier, response.Category);
        if (response.IsDismiss) return;
        if (_lock?.IsLocked == true && NeedsWindow(response))
        {
            _waiting.Add(response);
            services.GetService<GtkNavigationService>()?.MainWindow?.Window.Present();
            Serilog.Log.Information("App lock: a notification response waits for the unlock.");
            return;
        }
        Handle(response);
    }

    // Mail and snooze actions run under the shared app lock rules; opening and composing need the unlocked window.
    private static bool NeedsWindow(NotificationResponse response)
        => response.IsOpen || (NotificationActionIdentifiers.TryParseMailAction(response.ActionIdentifier, out var operation)
            && NotificationContentRules.IsReplyAction(operation));

    private void Flush()
    {
        var waiting = _waiting.ToArray();
        _waiting.Clear();
        foreach (var response in waiting) Handle(response);
    }

    private void Handle(NotificationResponse response)
    {
        if (NeedsWindow(response)) services.GetService<GtkNavigationService>()?.MainWindow?.Window.Present();
        _ = HandleAsync(response);
    }

    private async Task HandleAsync(NotificationResponse response)
    {
        try { await services.GetRequiredService<INotificationResponseService>().HandleAsync(response); }
        catch (Exception exception) { Serilog.Log.Error(exception, "A notification response failed."); }
    }
}
