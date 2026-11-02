using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Views.Calendar;
using Wino.Mail.UIKit.Views.Contacts;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Runs Home Screen quick actions (the Mac Dock menu's New Mail and New Event): the create actions do what the shell's
/// floating button does in that mode, the next meeting opens like a calendar notification tap. Actions that arrive
/// while the app is launching wait until <see cref="SetReady"/>.
/// </summary>
public sealed class UIKitQuickActionCoordinator(
    IServiceProvider services,
    IApplicationShellHost shellHost,
    UIKitNavigationService navigation,
    IUIKitShellHost host,
    IDispatcher dispatcher)
{
    private static readonly TimeSpan ContentTimeout = TimeSpan.FromSeconds(10);
    private readonly List<UIApplicationShortcutItem> _pending = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private bool _ready;

    public Task HandleAsync(UIApplicationShortcutItem item)
    {
        lock (_lock)
        {
            if (!_ready)
            {
                _pending.Add(item);
                return Task.CompletedTask;
            }
        }
        return RunAsync(item);
    }

    public async Task SetReady()
    {
        UIApplicationShortcutItem[] pending;
        lock (_lock)
        {
            _ready = true;
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var item in pending) await RunAsync(item);
    }

    private async Task RunAsync(UIApplicationShortcutItem item)
    {
        if (!QuickActionIdentifiers.TryParse(item, out var kind, out var calendarItemId))
        {
            Serilog.Log.Warning("Unknown quick action {Type}", item.Type);
            return;
        }
        await _gate.WaitAsync();
        try
        {
            if (kind == QuickActionKind.NextMeeting)
            {
                if (navigation.IsShellReady && !await CloseSheetsAsync()) return;
                if (calendarItemId is { } id) await services.GetRequiredService<INotificationResponseService>().OpenCalendarItemAsync(id);
                return;
            }
            var mode = kind switch
            {
                QuickActionKind.NewEvent => WinoApplicationMode.Calendar,
                QuickActionKind.NewTask => WinoApplicationMode.Tasks,
                _ => WinoApplicationMode.Mail
            };
            if (navigation.IsShellReady && !await CloseSheetsAsync()) return;
            if (!await shellHost.EnsureShellAsync(mode))
            {
                Serilog.Log.Information("Quick action {Kind} skipped: the shell is not available.", kind);
                return;
            }
            if (navigation.Shell?.ContentSettled is { } settled && await Task.WhenAny(settled, Task.Delay(ContentTimeout)) != settled)
            {
                Serilog.Log.Information("Quick action {Kind} skipped: the {Mode} page did not appear.", kind, mode);
                return;
            }
            await dispatcher.ExecuteOnUIThread(() => navigation.Shell?.CreateNew());
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Quick action {Kind} failed.", kind); }
        finally { _gate.Release(); }
    }

    /// <summary>False when an open editor (a draft, an event or a contact being edited) keeps the screen.</summary>
    private async Task<bool> CloseSheetsAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await dispatcher.ExecuteOnUIThread(() => host.CloseSheetsAsync(IsEditor).ContinueWith(task =>
        {
            if (task.Exception is { } error) completion.TrySetException(error.InnerExceptions);
            else completion.TrySetResult(task.Result);
        }, TaskScheduler.Default));
        return await completion.Task;
    }

    private static bool IsEditor(UIViewController controller)
        => controller is ComposePageViewController or CalendarEventComposePageViewController or ContactEditPageViewController;
}
