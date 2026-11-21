using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux;
using Wino.Platform.Linux.Authentication;
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
    private readonly List<Uri> _pendingCallbacks = [];
    private MainWindow? _window;
    private ServiceProvider? _services;
    private InfoBarMessageRelay? _infoBarRelay;
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
                if (LinuxActivation.TryGetAuthCallback(file, out var callback))
                {
                    DeliverAuthCallback(callback);
                    continue;
                }
                if (LinuxActivation.Classify(file) is { } item) _pendingActivations.Add(item);
            }
            ShowWindow();
            _ = RouteActivationsAsync();
        };
        AppActions.Register(application, () => _services, () => _window);
        ShellModeActions.Register(application, () => _services);
#if DEBUG
        if (options.DebugActions)
        {
            Infrastructure.Diagnostics.DebugActions.Register(application, () => _window?.Window);
            Infrastructure.Diagnostics.FoundationDebugActions.Register(application);
            Infrastructure.Diagnostics.MenuDebugActions.Register(application);
            Infrastructure.Diagnostics.NavigationDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.DialogDebugActions.Register(application, () => _services, () => _window?.Window);
            Infrastructure.Diagnostics.IconDebugActions.Register(application);
            Infrastructure.Diagnostics.ThemeDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.AvatarDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.InfoBarDebugActions.Register(application, () => _services, () => _window);
            Infrastructure.Diagnostics.ShellDebugActions.Register(application, () => _services, () => _window);
            Infrastructure.Diagnostics.HeaderDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.SettingsDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ListDebugActions.Register(application);
            Infrastructure.Diagnostics.SettingsRowsDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.PrivacyDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.NotificationDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ModeDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.SidebarDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.SidebarMenuDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ShortcutDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.WindowDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.LanguageDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailRowDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.BackupDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.SignatureDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailListDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ShortcutSettingsDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailSelectionDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ThemeEditorDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ListPickerDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.AccountDialogDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.FolderCategoryDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailFiltersDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.AccountsDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailListHeaderDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.SignInDebugActions.Register(application, () => _services);
        }
#endif
    }

    // The query carries the completion code: only the path is ever logged.
    private void DeliverAuthCallback(Uri callback)
    {
        // Before startup nothing can wait and nothing logs yet; the address is reported once the services run.
        if (!_started)
        {
            _pendingCallbacks.Add(callback);
            return;
        }
        if (_services?.GetService<LinuxExternalAuthCallbacks>()?.Deliver(callback) == true)
        {
            Serilog.Log.Information("A sign-in redirect arrived (path {Path}).", callback.AbsolutePath);
            return;
        }
        Serilog.Log.Warning("A sign-in redirect arrived while nothing was waiting for it (path {Path}).", callback.AbsolutePath);
    }

    private void ShowWindow()
    {
        if (_window is not null)
        {
            _window.Window.Present();
            return;
        }

        LinuxLanguage.ApplyDirection(LinuxLanguage.Prepared);
        Wino.Presentation.Linux.WinoIcons.Install();
        _window = new MainWindow(_application, WindowStateStore.LoadPlacement(LinuxPaths.FromEnvironment().ApplicationDataRoot));
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
                window.ShowStatus("dialog-password-symbolic", Translator.Linux_KeyringUnavailable_Title,
                    Translator.Linux_KeyringUnavailable_Message + "\n\n" + exception.Message);
                return;
            }

            _services = LinuxComposition.Create(bootstrap, () => _window, _options);
            _infoBarRelay = InfoBarMessageRelay.Start(_services.GetRequiredService<IMessenger>(), _services.GetRequiredService<IMailDialogService>());
            await _services.GetRequiredService<ApplicationLifecycleCoordinator>().StartAsync(_services.GetRequiredService<GtkApplicationLifecycleHost>());
            LogIconFonts();
            _started = true;
            foreach (var callback in _pendingCallbacks) DeliverAuthCallback(callback);
            _pendingCallbacks.Clear();
            await RouteActivationsAsync();
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Startup failed.");
            window.ShowStatus("dialog-error-symbolic", Translator.Linux_StartupFailed_Title, exception.Message);
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
        if (_window is { } window)
        {
            try { _services.GetRequiredService<WindowStateStore>().SavePlacement(window.CapturePlacement()); }
            catch (Exception exception) { Serilog.Log.Error(exception, "The window placement could not be saved."); }
        }
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
