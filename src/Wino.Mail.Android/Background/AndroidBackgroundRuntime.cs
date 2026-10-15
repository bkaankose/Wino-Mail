using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Android.Background;

/// <summary>
/// The shared services for work that runs without an activity (notification actions, reminder alarms, the queued
/// changes flush, relay pushes). The process may have been started for that work alone, so the logger is set up and
/// the runtime is initialized (database, synchronizers) without its periodic loops; when an activity starts later, the
/// normal startup order (<see cref="ApplicationLifecycleCoordinator"/>) starts the loops on the same runtime.
/// </summary>
internal static class AndroidBackgroundRuntime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _loggerReady;

    /// <summary>The services with an initialized runtime; null when the application has no container (it always has one after OnCreate).</summary>
    public static async Task<IServiceProvider?> EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (MainApplication.Services is not { } services) return null;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var coordinator = services.GetRequiredService<ApplicationLifecycleCoordinator>();
            // The foreground startup sets the logger up itself; a background-only process does it here once.
            if (!_loggerReady && MainApplication.Lifecycle?.Startup is null)
                services.GetRequiredService<IWinoLogger>().SetupLogger(coordinator.LogFilePath);
            _loggerReady = true;
        }
        finally { Gate.Release(); }

        await services.GetRequiredService<IApplicationRuntime>().InitializeAsync(cancellationToken);
        // A change queued by this work (a notification action) still gets its flush.
        services.GetRequiredService<Wino.Platform.Android.Background.AndroidPendingChangesScheduler>().Start();
        return services;
    }
}
