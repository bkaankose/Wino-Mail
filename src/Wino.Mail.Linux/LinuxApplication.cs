using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux;
using Wino.Platform.Linux.Security;

namespace Wino.Mail.Linux;

/// <summary>
/// The application: one window, the container, and the shared startup and shutdown order. Activations that arrive
/// before the shell is up (a <c>wino://</c> address on the command line) wait until startup finished.
/// </summary>
internal sealed class LinuxApplication
{
    private readonly Adw.Application _application;
    private readonly LaunchOptions _options;
    private readonly List<ActivationItem> _pendingActivations = [];
    private MainWindow? _window;
    private ServiceProvider? _services;
    private bool _started;
    private bool _stopping;

    public LinuxApplication(Adw.Application application, LaunchOptions options)
    {
        _application = application;
        _options = options;
        application.OnActivate += (_, _) => ShowWindow();
        application.OnOpen += (sender, args) =>
        {
            foreach (var file in args.Files)
            {
                if (LinuxActivation.Classify(file) is { } item) _pendingActivations.Add(item);
            }
            ShowWindow();
            _ = RouteActivationsAsync();
        };
#if DEBUG
        if (options.DebugActions)
        {
            Infrastructure.Diagnostics.DebugActions.Register(application, () => _window?.Window);
            Infrastructure.Diagnostics.FoundationDebugActions.Register(application);
            Infrastructure.Diagnostics.MenuDebugActions.Register(application);
            Infrastructure.Diagnostics.NavigationDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.DialogDebugActions.Register(application, () => _services, () => _window?.Window);
            Infrastructure.Diagnostics.IconDebugActions.Register(application);
        }
#endif
    }

    private void ShowWindow()
    {
        if (_window is not null)
        {
            _window.Window.Present();
            return;
        }

        Wino.Presentation.Linux.WinoIcons.Install();
        _window = new MainWindow(_application);
        _window.Window.OnCloseRequest += OnCloseRequest;
        _window.Window.Present();
        _ = StartAsync(_window);
    }

    private async Task StartAsync(MainWindow window)
    {
        try
        {
            LinuxPlatformBootstrap bootstrap;
            try
            {
                bootstrap = LinuxPlatformBootstrap.Create(Program.ApplicationId);
            }
            catch (Exception exception) when (exception is SecretServiceException or DllNotFoundException)
            {
                // There is no unprotected fallback: without the keyring the app holds no credentials at all.
                window.ShowStatus("dialog-password-symbolic", "The system keyring is not available",
                    "Wino Mail keeps its encryption key in the keyring (Secret Service). Unlock or install a keyring such as GNOME Keyring, then start Wino Mail again.\n\n" + exception.Message);
                return;
            }

            _services = LinuxComposition.Create(bootstrap, () => _window, _options);
            await _services.GetRequiredService<ApplicationLifecycleCoordinator>().StartAsync(_services.GetRequiredService<GtkApplicationLifecycleHost>());
            LogIconFonts();
            _started = true;
            await RouteActivationsAsync();
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Startup failed.");
            window.ShowStatus("dialog-error-symbolic", "Wino Mail could not start", exception.Message);
        }
    }

    private static void LogIconFonts()
    {
        var check = Wino.Presentation.Linux.WinoIcons.Verify();
        if (check.Ok) Serilog.Log.Information("Wino icon fonts: {Detail}", check.Detail);
        else Serilog.Log.Error("Wino icon fonts are not available: {Detail}", check.Detail);
    }

    private async Task RouteActivationsAsync()
    {
        if (!_started || _services is null || _pendingActivations.Count == 0) return;
        var items = _pendingActivations.ToArray();
        _pendingActivations.Clear();
        try { await _services.GetRequiredService<ActivationRouter>().HandleAsync(items); }
        catch (Exception exception) { Serilog.Log.Error(exception, "An activation could not be handled."); }
    }

    // The first close hides the window and runs the shared shutdown; the application quits when it finished.
    private bool OnCloseRequest(Gtk.Window sender, EventArgs args)
    {
        if (_stopping || _services is null) return false;
        _stopping = true;
        sender.SetVisible(false);
        _ = StopAsync(_services);
        return true;
    }

    private async Task StopAsync(ServiceProvider services)
    {
        try
        {
            await services.GetRequiredService<ApplicationLifecycleCoordinator>().StopAsync(services.GetRequiredService<GtkApplicationLifecycleHost>());
            await services.DisposeAsync();
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Shutdown failed."); }
        finally
        {
            Serilog.Log.CloseAndFlush();
            _application.Quit();
        }
    }
}
