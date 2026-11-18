#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Lifecycle;

/// <summary>The first window content after startup.</summary>
public enum ApplicationStartupTarget
{
    /// <summary>At least one account exists: the shell.</summary>
    Shell,

    /// <summary>No accounts: the Welcome flow.</summary>
    Welcome
}

/// <summary>When startup starts the runtime's periodic work.</summary>
public enum ApplicationRuntimeStart
{
    /// <summary>The runtime starts and the theme loads before the first window (macOS, iOS, Android, Wino.Hosting).</summary>
    BeforeFirstWindow,

    /// <summary>
    /// The runtime is only initialized. The head loads the theme with its first window and calls
    /// <see cref="ApplicationLifecycleCoordinator.StartRuntimeAsync"/> once that window has rendered, so the first
    /// synchronization does not compete with the first frame (Windows).
    /// </summary>
    AfterFirstWindow
}

/// <summary>Head feature work that runs when services are ready and when the app quits.</summary>
public interface IApplicationLifecycleHook
{
    /// <summary>Right after the runtime started (or initialized, see <see cref="ApplicationRuntimeStart"/>); services are available.</summary>
    Task ServicesReadyAsync();

    /// <summary>
    /// At the start of quitting, while services are still alive. <paramref name="runtimeStarted"/> tells
    /// whether <see cref="ServicesReadyAsync"/> ran.
    /// </summary>
    Task StoppingAsync(bool runtimeStarted);
}

/// <summary>The head side of startup and shutdown: its feature hooks plus the window work only it can do.</summary>
public interface IApplicationLifecycleHost : IApplicationLifecycleHook
{
    /// <summary>
    /// Shows the first window content for <paramref name="target"/>. A head whose first window depends on how the
    /// process was activated (Windows: a toast, a share, a file, a logon start without any window) records the
    /// target here and shows the window from its activation route right after startup returns.
    /// </summary>
    Task ShowStartupAsync(ApplicationStartupTarget target);

    /// <summary>Stops navigation and closes windows; runs after <see cref="IApplicationLifecycleHook.StoppingAsync"/>.</summary>
    Task StopNavigationAsync();
}

/// <summary>
/// The startup and shutdown order every head follows. Startup: logger, runtime, theme, the head's
/// services-ready hooks, then the shell when accounts exist or Welcome otherwise. Shutdown: the head's
/// stopping hooks, navigation, then the runtime when it started. The head disposes the container afterwards.
/// The runtime, theme and account services are resolved after the logger is set up, so services that
/// take a logger context in their constructor log to the configured sinks.
/// </summary>
public sealed class ApplicationLifecycleCoordinator(
    IServiceProvider services,
    IWinoLogger logger,
    IApplicationConfiguration configuration)
{
    private bool _loggerConfigured;
    private bool _stopsRuntime;

    /// <summary>Log file below the application data folder.</summary>
    public string LogFilePath => Path.Combine(configuration.ApplicationDataFolderPath, "Logs", "wino.log");

    /// <summary>
    /// The runtime started and the theme loaded (with <see cref="ApplicationRuntimeStart.AfterFirstWindow"/>: the
    /// runtime initialized); services-ready hooks have run or are running.
    /// </summary>
    public bool RuntimeStarted { get; private set; }

    /// <summary>
    /// Sets the logger up once. Startup calls it with <see cref="LogFilePath"/>; a head that logs before startup
    /// (Windows, from its application constructor) calls it earlier with its own file, and startup keeps that.
    /// </summary>
    public void SetupLogger(string? logFilePath = null)
    {
        if (_loggerConfigured) return;

        logger.SetupLogger(logFilePath ?? LogFilePath);
        _loggerConfigured = true;
    }

    public async Task<ApplicationStartupTarget> StartAsync(
        IApplicationLifecycleHost host,
        ApplicationRuntimeStart runtimeStart = ApplicationRuntimeStart.BeforeFirstWindow)
    {
        SetupLogger();

        var runtime = services.GetRequiredService<IApplicationRuntime>();
        if (runtimeStart == ApplicationRuntimeStart.AfterFirstWindow)
        {
            // Set before initializing: quitting during initialization stops the runtime, whose lifetime token
            // then cancels the remaining initialization steps.
            _stopsRuntime = true;
            await runtime.InitializeAsync();
        }
        else
        {
            await runtime.StartAsync();
            await services.GetRequiredService<INewThemeService>().InitializeAsync();
            _stopsRuntime = true;
        }

        RuntimeStarted = true;

        await host.ServicesReadyAsync();

        var target = await GetStartupTargetAsync();
        await host.ShowStartupAsync(target);
        return target;
    }

    /// <summary>Starts the runtime's periodic work after a startup with <see cref="ApplicationRuntimeStart.AfterFirstWindow"/>.</summary>
    public Task StartRuntimeAsync() => services.GetRequiredService<IApplicationRuntime>().StartAsync();

    /// <summary>The shell when at least one account exists, Welcome otherwise.</summary>
    public async Task<ApplicationStartupTarget> GetStartupTargetAsync()
    {
        var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
        return accounts.Count > 0 ? ApplicationStartupTarget.Shell : ApplicationStartupTarget.Welcome;
    }

    public async Task StopAsync(IApplicationLifecycleHost host)
    {
        await host.StoppingAsync(RuntimeStarted);
        await host.StopNavigationAsync();

        if (_stopsRuntime) await services.GetRequiredService<IApplicationRuntime>().StopAsync();
    }
}
