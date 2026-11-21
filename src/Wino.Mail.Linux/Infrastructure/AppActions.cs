using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views;

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

    private const string DeveloperName = "Burak Kaan Köse";

    public static string Detailed(string name) => "app." + name;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services, Func<MainWindow?> window)
    {
        Add(application, Preferences, () => OpenPreferences(services()));
        Add(application, Shortcuts, static () => { });
        Add(application, WhatsNew, () => Shell(services())?.HandleAction(WhatsNew));
        Add(application, About, () => ShowAbout(services(), window()));
        Add(application, Quit, () =>
        {
            if (window() is { } main) main.Window.Close();
            else application.Quit();
        });
        foreach (var name in new[] { ToggleSidebar, Synchronize, Back, Search, Briefing, Account })
        {
            Add(application, name, () => Shell(services())?.HandleAction(name));
        }

        SetEnabled(application, Shortcuts, false);
        SetEnabled(application, WhatsNew, false);

        application.SetAccelsForAction(Detailed(Preferences), ["<Control>comma"]);
        application.SetAccelsForAction(Detailed(Quit), ["<Control>q"]);
        application.SetAccelsForAction(Detailed(Back), ["<Alt>Left"]);
        application.SetAccelsForAction(Detailed(ToggleSidebar), ["F9"]);
        application.SetAccelsForAction(Detailed(Search), ["<Control>f"]);
    }

    public static void SetEnabled(Gio.Application? application, string name, bool enabled)
    {
        if (application?.LookupAction(name) is Gio.SimpleAction action) action.SetEnabled(enabled);
    }

    // Actions live as long as the application, so a plain handler cannot keep a view alive.
    private static void Add(Adw.Application application, string name, Action activated)
    {
        var action = Gio.SimpleAction.New(name, null);
        action.OnActivate += (_, _) =>
        {
            try { activated(); }
            catch (Exception exception) { Serilog.Log.Error(exception, "The application action {Action} failed.", name); }
        };
        application.AddAction(action);
    }

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
        if (services?.GetService<IAppMetadataService>() is { } metadata) dialog.SetVersion(metadata.AppVersion);
        dialog.SetWebsite(AppUrls.Website);
        dialog.SetIssueUrl(AppUrls.GitHub.TrimEnd('/') + "/issues");
        dialog.SetDeveloperName(DeveloperName);
        dialog.Present(window?.Window);
    }
}
