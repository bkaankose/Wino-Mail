using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Android.Background;
using Wino.Platform.Android.Notifications;

namespace Wino.Mail.Android;

/// <summary>
/// Runs the shared startup and shutdown order (<see cref="ApplicationLifecycleCoordinator"/>) by process visibility
/// (plan section 5): the runtime starts when the first activity starts, and stops after <see cref="GracePeriod"/> with no
/// started activity. The shared runtime cannot start again once stopped, so after a background stop the process ends;
/// the system recreates the task (and this composition) the next time the user opens the app.
/// Leaving the foreground enqueues the queued-changes flush and sets the reminder alarm, because the system may end
/// the process at any time after that; the stop itself sends queued changes first.
/// </summary>
internal sealed class AndroidForegroundLifecycle
{
    /// <summary>Long enough for a sign-in in a Custom Tab or a quick trip to another app.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(10);

    /// <summary>How long the stop waits for queued changes to reach their servers.</summary>
    private static readonly TimeSpan StopFlushBudget = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly ApplicationLifecycleCoordinator _coordinator;
    private readonly AndroidApplicationLifecycleHost _host;
    private Task? _startup;
    private CancellationTokenSource? _pendingStop;
    private bool _stopping;

    public AndroidForegroundLifecycle(IServiceProvider services, AndroidActivityTracker activities, ApplicationLifecycleCoordinator coordinator,
        AndroidApplicationLifecycleHost host)
    {
        _services = services;
        _coordinator = coordinator;
        _host = host;
        activities.ForegroundChanged += (_, foreground) =>
        {
            if (foreground) EnterForeground();
            else EnterBackground();
        };
    }

    /// <summary>Completes when startup finished (or failed); null before the first activity started.</summary>
    public Task? Startup => _startup;

    private void EnterForeground()
    {
        _pendingStop?.Cancel();
        _pendingStop = null;
        if (_stopping) return;
        _startup ??= StartAsync();
    }

    private void EnterBackground()
    {
        if (_startup is null || _stopping) return;
        _pendingStop?.Cancel();
        var pending = new CancellationTokenSource();
        _pendingStop = pending;
        _ = StopAfterGracePeriodAsync(pending.Token);
        _ = PrepareForBackgroundAsync();
    }

    /// <summary>The flush and the reminder alarm, in case the system ends the process before the grace period does.</summary>
    private async Task PrepareForBackgroundAsync()
    {
        try
        {
            if (_startup is { } startup) await startup;
            if (!_coordinator.RuntimeStarted) return;
            _services.GetRequiredService<AndroidPendingChangesScheduler>().EnqueueIfPending("left the foreground");
            await _services.GetRequiredService<AndroidReminderScheduler>().ScheduleNextAsync();
        }
        catch (Exception error) { Serilog.Log.Warning(error, "Preparing for the background failed."); }
    }

    /// <summary>The grace period; Debug builds can shorten it (<c>DebugCommand --es command graceperiod --ei seconds N</c>).</summary>
    private static TimeSpan EffectiveGracePeriod
    {
        get
        {
#if DEBUG
            if (Views.Debug.DebugSettings.GracePeriodOverride is { } debugOverride) return debugOverride;
#endif
            return GracePeriod;
        }
    }

    private async Task StartAsync()
    {
        try
        {
            var target = await _coordinator.StartAsync(_host);
            Serilog.Log.Information("Wino Mail Android started: {Target}", target);
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Error("WinoMail", "Startup failed: " + error);
            Serilog.Log.Fatal(error, "Wino Mail Android startup failed.");
        }
    }

    private async Task StopAfterGracePeriodAsync(CancellationToken cancellation)
    {
        var gracePeriod = EffectiveGracePeriod;
        try { await Task.Delay(gracePeriod, cancellation); }
        catch (OperationCanceledException) { return; }
        if (_stopping) return;
        _stopping = true;
        try
        {
            if (_startup is { } startup) await startup;
            await FlushBeforeStopAsync();
            await _coordinator.StopAsync(_host);
            Serilog.Log.Information("Wino Mail Android stopped after {Seconds} seconds in the background.", gracePeriod.TotalSeconds);
            await Serilog.Log.CloseAndFlushAsync();
        }
        catch (Exception error) { Serilog.Log.Warning(error, "Stopping Wino Mail Android failed."); }
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    /// <summary>Queued changes live in memory and end with the process: send them before it ends.</summary>
    private async Task FlushBeforeStopAsync()
    {
        if (!_coordinator.RuntimeStarted) return;
        try
        {
            using var budget = new CancellationTokenSource(StopFlushBudget);
            var flushed = await _services.GetRequiredService<IApplicationRuntime>().FlushQueuedRequestsAsync(budget.Token);
            Serilog.Log.Information("Queued changes before the stop: {State}.", flushed ? "nothing left" : "some could not be sent");
        }
        catch (Exception error) { Serilog.Log.Warning(error, "Sending queued changes before the stop failed."); }
    }
}
