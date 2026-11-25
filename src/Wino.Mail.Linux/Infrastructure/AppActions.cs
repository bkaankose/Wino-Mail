using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Infrastructure.Updates;
using Wino.Mail.Linux.Views;
using Wino.Platform.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The application actions behind the primary menu and the accelerators. Each handler resolves what it needs when it
/// runs and does nothing before the container exists (startup, the keyring error page). Accelerators are set here
/// only, so a key has one owner.
/// </summary>
internal static class AppActions
{
    public const string ApplicationName = "Wino Mail";

    public const string Preferences = "preferences";
    public const string Shortcuts = "shortcuts";
    public const string WhatsNew = "whats-new";
    public const string About = "about";
    public const string Quit = "quit";
    public const string ToggleSidebar = "toggle-sidebar";
    public const string Synchronize = "sync";
    public const string Back = "back";
    public const string Search = "search";
    public const string Briefing = "briefing";
    public const string Account = "account";
    public const string CheckForUpdates = "check-updates";

    private const string DeveloperName = "Burak Kaan Köse";

    public static string Detailed(string name) => "app." + name;

    /// <summary>Whether this copy came from a package with an update channel; a development build has no update check.</summary>
    public static bool HasUpdates => LinuxDistribution.Current.IsPackaged && LinuxUpdateService.Kind is not null;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services, Func<MainWindow?> window, Action quit)
    {
        Add(application, window, Preferences, () => OpenPreferences(services()));
        Add(application, window, Shortcuts, static () => { });
        Add(application, window, WhatsNew, () => Shell(services())?.HandleAction(WhatsNew));
        Add(application, window, CheckForUpdates, () => _ = services()?.GetService<LinuxUpdatePresenter>()?.CheckNowAsync());
        Add(application, window, About, () => ShowAbout(services(), window()));
        Add(application, window, Quit, quit);
        Add(application, window, LinuxKeymap.CloseWindow, () => CloseActiveWindow(application));
        // Search acts on the window that has the focus: the find bar of a pop-out composer or reader, else the shell.
        AddUnguarded(application, Search, () =>
        {
            var active = ActiveWindow(application);
            if (active is not null && window()?.Window is { } main && !IsSame(active, main))
            {
                FindInPageTargets.TryOpen(active);
                return;
            }
            if (window()?.Window is { IsActive: true }) Shell(services())?.HandleAction(Search);
        });
        foreach (var name in new[] { ToggleSidebar, Synchronize, Back, Briefing, Account })
        {
            Add(application, window, name, () => Shell(services())?.HandleAction(name));
        }
        foreach (var key in LinuxKeymap.All)
        {
            if (key.Scope != LinuxKeyScope.Context || application.LookupAction(key.Action) is not null) continue;
            var name = key.Action;
            Add(application, window, name, () => Shell(services())?.HandleAction(name));
        }

        SetEnabled(application, Shortcuts, false);
        SetEnabled(application, WhatsNew, false);
        SetEnabled(application, CheckForUpdates, HasUpdates);

        foreach (var key in LinuxKeymap.All)
        {
            if (key.Scope == LinuxKeyScope.Global && key.Action != Shortcuts && !key.Action.StartsWith("mode-", StringComparison.Ordinal))
                application.SetAccelsForAction(Detailed(key.Action), key.Accelerators);
        }
    }

    public static void SetEnabled(Gio.Application? application, string name, bool enabled)
    {
        if (application?.LookupAction(name) is Gio.SimpleAction action) action.SetEnabled(enabled);
    }

    /// <summary>The window of the application that has the keyboard focus; null when none has (Settings is not an application window).</summary>
    public static Gtk.Window? ActiveWindow(Gtk.Application application)
    {
        var candidate = application.GetActiveWindow();
        return candidate is { IsActive: true } ? candidate : null;
    }

    public static bool IsSame(Gtk.Widget a, Gtk.Widget b) => a.Handle.DangerousGetHandle() == b.Handle.DangerousGetHandle();

    // App accelerators fire in every window that has the application, so the shell's actions act only while the main
    // window has the focus: Ctrl+F, F9 or Alt+Left in a pop-out must not change the shell behind it.
    private static void Add(Adw.Application application, Func<MainWindow?> window, string name, Action activated)
    {
        if (!LinuxKeymap.IsShellAction(name) || name is WhatsNew or Shortcuts)
        {
            AddUnguarded(application, name, activated);
            return;
        }
        AddUnguarded(application, name, () =>
        {
            if (window()?.Window is { IsActive: true }) activated();
            else Serilog.Log.Debug("The application action {Action} was ignored: the main window is not active.", name);
        });
    }

    // Actions live as long as the application, so a plain handler cannot keep a view alive.
    private static void AddUnguarded(Adw.Application application, string name, Action activated)
    {
        var action = Gio.SimpleAction.New(name, null);
        action.OnActivate += (_, _) =>
        {
            try { activated(); }
            catch (Exception exception) { Serilog.Log.Error(exception, "The application action {Action} failed.", name); }
        };
        application.AddAction(action);
    }

    // Close goes through each window's own close request: Settings asks ShouldClose, the main window runs in the
    // background or quits, a secondary window releases its view.
    private static void CloseActiveWindow(Gtk.Application application) => ActiveWindow(application)?.Close();

    private static Views.Shell.ShellView? Shell(IServiceProvider? services) => services?.GetService<GtkNavigationService>()?.Shell;

    private static void OpenPreferences(IServiceProvider? services)
    {
        if (services?.GetService<INavigationService>() is not { } navigation) return;
        if (!navigation.ChangeApplicationMode(WinoApplicationMode.Settings)) Serilog.Log.Information("Settings are not available.");
    }

    private static void ShowAbout(IServiceProvider? services, MainWindow? window)
    {
        var dialog = Adw.AboutDialog.New();
        dialog.SetApplicationName(ApplicationName);
        if (services?.GetService<IAppMetadataService>() is { } metadata)
            dialog.SetVersion(MarketingVersion(metadata.AppVersion) + (LinuxDistribution.Current.IsBeta ? " (Beta)" : string.Empty));
        dialog.SetWebsite(AppUrls.Website);
        dialog.SetIssueUrl(AppUrls.GitHub.TrimEnd('/') + "/issues");
        dialog.SetDeveloperName(DeveloperName);
        dialog.Present(window?.Window);
    }

    // The informational version carries "+<commit hash>"; Windows and macOS show the marketing version only.
    private static string MarketingVersion(string version)
        => version.IndexOf('+') is var plus and > 0 ? version[..plus] : version;
}
