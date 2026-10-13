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

/// <summary>Head feature work that runs when services are ready and when the app quits.</summary>
public interface IApplicationLifecycleHook
{
    /// <summary>Right after the runtime started and the theme loaded; services are available.</summary>
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
    /// <summary>Shows the first window content for <paramref name="target"/>.</summary>
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
    /// <summary>Log file below the application data folder.</summary>
    public string LogFilePath => Path.Combine(configuration.ApplicationDataFolderPath, "Logs", "wino.log");

    /// <summary>The runtime started and the theme loaded; services-ready hooks have run or are running.</summary>
    public bool RuntimeStarted { get; private set; }

    public async Task<ApplicationStartupTarget> StartAsync(IApplicationLifecycleHost host)
    {
        logger.SetupLogger(LogFilePath);

        await services.GetRequiredService<IApplicationRuntime>().StartAsync();
        await services.GetRequiredService<INewThemeService>().InitializeAsync();
        RuntimeStarted = true;

        await host.ServicesReadyAsync();

        var target = await GetStartupTargetAsync();
        await host.ShowStartupAsync(target);
        return target;
    }

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

        if (RuntimeStarted) await services.GetRequiredService<IApplicationRuntime>().StopAsync();
    }
}
