using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux;
using Wino.Platform.Linux.Authentication;
using Wino.Platform.Linux.Security;
using Wino.Platform.Linux.Services;

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
    private readonly List<LinuxLaunchCommand> _pendingCommands = [];
    private MainWindow? _window;
    private ServiceProvider? _services;
    private InfoBarMessageRelay? _infoBarRelay;
    private bool _started;
    private bool _stopping;
    private bool _quitRequested;
    private bool _closing;
    private bool _held;

    public LinuxApplication(Adw.Application application, LaunchOptions options)
    {
        _application = application;
        _options = options;
        application.OnActivate += (_, _) => ShowWindow(_options.Background);
        application.OnOpen += (sender, args) =>
        {
            foreach (var file in args.Files)
            {
                if (LinuxActivation.TryGetAuthCallback(file, out var callback))
                {
                    DeliverAuthCallback(callback);
                    continue;
                }
                if (LinuxActivation.TryGetCommand(file, out var command))
                {
                    if (command is not null) _pendingCommands.Add(command);
                    continue;
                }
                if (LinuxActivation.Classify(file) is { } item) _pendingActivations.Add(item);
            }
            ShowWindow(background: false);
            _ = RouteActivationsAsync();
        };
        AppActions.Register(application, () => _services, () => _window, Quit);
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
            Infrastructure.Diagnostics.DesktopProfileDebugActions.Register(application, () => _services);
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
            Infrastructure.Diagnostics.MailClientImportDebugActions.Register(application);
            Infrastructure.Diagnostics.AccountDialogDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.FolderCategoryDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailFiltersDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.AccountsDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.AccountPagesDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailListHeaderDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.SignInDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.EmbeddedSignInDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.GmailDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.AttentionDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.WinoAccountDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.WinoAccountPageDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.PurchaseDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.IntelligenceDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.IntelligenceGalleryDebugActions.Register(application);
            Infrastructure.Diagnostics.BriefingDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.WhatsNewDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ComposeRewriteDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailContextMenuDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.OutlookDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailHoverDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailSwipeDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailActionBarDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailFolderPickerDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.AccountSetupDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailTilesDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ImapSetupDebugActions.Register(application);
            Infrastructure.Diagnostics.SetupProgressDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailDragDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailSearchDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.MailSearchFilterDebugActions.Register(application);
            Infrastructure.Diagnostics.ReaderDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.EditorDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ComposeDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.RecipientDebugActions.Register(application);
            Infrastructure.Diagnostics.SuggestionDebugActions.Register(application);
            Infrastructure.Diagnostics.ToolbarDebugActions.Register(application);
            Infrastructure.Diagnostics.InsertDebugActions.Register(application);
            Infrastructure.Diagnostics.CalendarDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.AttachmentDebugActions.Register(application);
            Infrastructure.Diagnostics.FindDebugActions.Register(application);
            Infrastructure.Diagnostics.ComposeSignatureDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.PopOutComposeDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.RichEditorDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.EmailTemplateDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ContactsDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ToDoDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.ToDoGalleryDebugActions.Register(application);
            Infrastructure.Diagnostics.AppLockDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.NotifyDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.StartupDebugActions.Register(application, () => _services, () => _window, () => _held);
            Infrastructure.Diagnostics.SoundDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.SmimeDebugActions.Register(application, () => _services);
            Infrastructure.Diagnostics.LinkedItemsDebugActions.Register(application);
            Infrastructure.Diagnostics.ReminderEditorDebugActions.Register(application);
            Infrastructure.Diagnostics.UpdateDebugActions.Register(application, () => _services);
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

    private void ShowWindow(bool background)
    {
        if (_window is not null)
        {
            Reveal(_window);
            return;
        }

        LinuxLanguage.ApplyDirection(LinuxLanguage.Prepared);
        Wino.Presentation.Linux.WinoIcons.Install();
        // The icon is found without an installed desktop entry too (window, About, notifications).
        if (Gdk.Display.GetDefault() is { } display) Gtk.IconTheme.GetForDisplay(display).AddSearchPath(Path.Combine(AppContext.BaseDirectory, "icons"));
        Gtk.Window.SetDefaultIconName(Program.ApplicationId);
        var dataRoot = LinuxPaths.FromEnvironment().ApplicationDataRoot;
        WindowChrome.Apply(LinuxDesktopProfile.LoadEffectiveStyle(dataRoot));
        _window = new MainWindow(_application, WindowStateStore.LoadPlacement(dataRoot));
        _window.Window.OnCloseRequest += OnCloseRequest;
        // Launched at sign-in: services and synchronization run, the window waits for the next activation.
        if (background) Hold();
        else _window.Window.Present();
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
            catch (Exception exception) when (KeyringFailure(exception) is not null)
            {
                ShowKeyringUnavailable(window, exception);
                return;
            }

            _services = LinuxComposition.Create(bootstrap, () => _window, _options);
            DatabaseJournalRecovery.Recover(_services.GetRequiredService<IApplicationConfiguration>());
            _services.GetRequiredService<LinuxWindowPresence>().HasWindow = window.Window.GetVisible();
            _services.GetRequiredService<AppLockCoordinator>().Unlocked += () => _ = RouteActivationsAsync();
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
            // The keyring is first read when the services start, so a missing or locked one usually fails here, wrapped.
            if (KeyringFailure(exception) is not null) ShowKeyringUnavailable(window, exception);
            else window.ShowStatus("dialog-error-symbolic", Translator.Linux_StartupFailed_Title, exception.Message);
        }
    }

    private static Exception? KeyringFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SecretServiceException or DllNotFoundException) return current;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    if (KeyringFailure(inner) is { } found) return found;
        }
        return null;
    }

    // There is no unprotected fallback: without the keyring the app holds no credentials at all.
    private static void ShowKeyringUnavailable(MainWindow window, Exception exception)
        => window.ShowStatus("dialog-password-symbolic", Translator.Linux_KeyringUnavailable_Title,
            Translator.Linux_KeyringUnavailable_Message + "\n\n" + (KeyringFailure(exception) ?? exception).Message);

    private static void LogIconFonts()
    {
        var check = Wino.Presentation.Linux.WinoIcons.Verify();
        if (check.Ok) Serilog.Log.Information("Wino icon fonts: {Detail}", check.Detail);
        else Serilog.Log.Error("Wino icon fonts are not available: {Detail}", check.Detail);
    }

    // Windows jump list and companion entries: a mode, a new mail (OpenCompanionNewMailAsync), a new event.
    private static async Task RunCommandAsync(IServiceProvider services, LinuxLaunchCommand command)
    {
        var navigation = services.GetRequiredService<GtkNavigationService>();
        Serilog.Log.Information("Launch command {Command} ({Mode}).", command.Kind, command.Mode);
        switch (command.Kind)
        {
            case LinuxLaunchCommandKind.Compose:
                if (!await navigation.EnsureShellAsync(WinoApplicationMode.Mail)) return;
                var mail = services.GetRequiredService<MailAppShellViewModel>();
                Task create = Task.CompletedTask;
                await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => create = mail.HandleCreateNewMailAsync());
                await create;
                break;
            case LinuxLaunchCommandKind.NewEvent:
                var start = DateTime.Now;
                await navigation.EnsureShellAsync(WinoApplicationMode.Calendar, new CalendarEventComposeNavigationArgs
                {
                    StartDate = start,
                    EndDate = start.AddHours(1),
                    RequireCalendarPickerWhenUnresolved = true,
                });
                break;
            default:
                await navigation.EnsureShellAsync(command.Mode);
                break;
        }
    }

    private async Task RouteActivationsAsync()
    {
        if (!_started || _services is null || _pendingActivations.Count + _pendingCommands.Count == 0) return;
        if (_services.GetRequiredService<AppLockCoordinator>().IsLocked)
        {
            Serilog.Log.Information("App lock: {Count} activations wait for the unlock.", _pendingActivations.Count + _pendingCommands.Count);
            return;
        }
        var commands = _pendingCommands.ToArray();
        _pendingCommands.Clear();
        foreach (var command in commands)
        {
            try { await RunCommandAsync(_services, command); }
            catch (Exception exception) { Serilog.Log.Error(exception, "The launch command {Command} failed.", command.Kind); }
        }
        if (_pendingActivations.Count == 0) return;
        var items = _pendingActivations.ToArray();
        _pendingActivations.Clear();
        try { await _services.GetRequiredService<ActivationRouter>().HandleAsync(items); }
        catch (Exception exception) { Serilog.Log.Error(exception, "An activation could not be handled."); }
    }

    /// <summary>Quit from the menu or Ctrl+Q: the shared shutdown, also when closing would only hide the window.</summary>
    public void Quit()
    {
        _quitRequested = true;
        if (_window is { } window && !_stopping)
        {
            CloseDialogs(window.Window);
            window.Window.Close();
        }
        else if (_window is null) _application.Quit();
    }

    // AdwApplicationWindow answers a close request by closing its top dialog only, so quit closes every dialog first.
    private static void CloseDialogs(Adw.ApplicationWindow window)
    {
        for (var remaining = 16; remaining > 0 && window.GetVisibleDialog() is { } dialog; remaining--)
            dialog.ForceClose();
    }

    private void Reveal(MainWindow window)
    {
        if (_stopping) return;
        window.Window.Present();
        if (_services?.GetService<LinuxWindowPresence>() is { } presence) presence.HasWindow = true;
        Release();
    }

    // Closing keeps the process when the user chose to run in the background and an account exists (Windows
    // ShellWindow: pop-outs close and the lock session ends); otherwise it hides the window and runs the shared
    // shutdown, and the application quits after it. The account check is a database read, so the close request is
    // always held and decided asynchronously.
    private bool OnCloseRequest(Gtk.Window sender, EventArgs args)
    {
        if (_stopping || _services is null) return false;
        if (!_closing)
        {
            _closing = true;
            _ = CloseAsync(sender, _services);
        }
        return true;
    }

    private async Task CloseAsync(Gtk.Window sender, ServiceProvider services)
    {
        bool quit;
        try
        {
            // Without an account (Welcome and the first account setup) nothing would run in the background.
            quit = _quitRequested
                || services.GetRequiredService<IPreferencesService>().AppCloseBehavior == AppCloseBehavior.Terminate
                || await services.GetRequiredService<ApplicationLifecycleCoordinator>().GetStartupTargetAsync() == ApplicationStartupTarget.Welcome;
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The close behavior could not be decided; Wino Mail quits.");
            quit = true;
        }
        finally
        {
            _closing = false;
        }
        if (_stopping) return;
        if (_window is { } window)
        {
            try { services.GetRequiredService<WindowStateStore>().SavePlacement(window.CapturePlacement()); }
            catch (Exception exception) { Serilog.Log.Error(exception, "The window placement could not be saved."); }
        }
        services.GetRequiredService<LinuxWindowPresence>().HasWindow = false;
        if (!quit)
        {
            sender.SetVisible(false);
            Hold();
            services.GetRequiredService<IAppLockService>().LockSession();
            _ = CloseSurfacesAsync(services);
            Serilog.Log.Information("The window closed; Wino Mail keeps running in the background.");
            return;
        }
        _stopping = true;
        sender.SetVisible(false);
        _ = StopAsync(services);
    }

    private static async Task CloseSurfacesAsync(ServiceProvider services)
    {
        try
        {
            await services.GetRequiredService<WindowManager>().CloseAllAsync();
            await services.GetRequiredService<GtkNavigationService>().CloseSettingsAsync();
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "The windows could not be closed."); }
    }

    private void Hold()
    {
        if (_held) return;
        _held = true;
        _application.Hold();
    }

    private void Release()
    {
        if (!_held) return;
        _held = false;
        _application.Release();
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
            Release();
            _application.Quit();
        }
    }
}
