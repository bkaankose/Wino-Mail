using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux.Services;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Owns the main window's <see cref="ShortcutRouter"/> and the Keyboard shortcuts overview (<c>app.shortcuts</c>, F1 and
/// Ctrl+?). Other windows construct their own router with <see cref="ShortcutRouterContext.IsPopOut"/>.
/// </summary>
internal sealed class ShortcutHost(IServiceProvider services, IDispatcher dispatcher) : IApplicationLifecycleHook
{
    private IDisposable? _overviewAction;

    public ShortcutRouter? MainRouter { get; private set; }

    public async Task ServicesReadyAsync()
    {
        var shortcuts = services.GetRequiredService<IKeyboardShortcutService>();
        await shortcuts.InitializeAsync();
        try { await ShortcutSeeds.SeedAsync(shortcuts, services.GetRequiredService<IConfigurationService>(), force: false); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Keyboard shortcuts: the Linux defaults could not be added."); }
        await dispatcher.ExecuteOnUIThread(() => Attach(shortcuts));
    }

    public async Task StoppingAsync(bool runtimeStarted)
    {
        await dispatcher.ExecuteOnUIThread(() =>
        {
            MainRouter?.Dispose();
            MainRouter = null;
            _overviewAction?.Dispose();
            _overviewAction = null;
            AppActions.SetEnabled(Gio.Application.GetDefault(), AppActions.Shortcuts, false);
        });
    }

    /// <summary>Shows the overview over the window that has the focus (a pop-out or the main window).</summary>
    public void ShowOverview()
    {
        var navigation = services.GetRequiredService<GtkNavigationService>();
        if (navigation.MainWindow?.Window is not { } window || window.GetApplication() is not { } application) return;
        var owner = AppActions.ActiveWindow(application) ?? window;
        ShortcutsOverview.Present(owner, services.GetRequiredService<IKeyboardShortcutService>(), application, services.GetRequiredService<SingleKeyShortcuts>().IsEnabled);
    }

    private void Attach(IKeyboardShortcutService shortcuts)
    {
        var navigation = services.GetRequiredService<GtkNavigationService>();
        if (navigation.MainWindow?.Window is not { } window)
        {
            Serilog.Log.Warning("Keyboard shortcuts: the main window does not exist; no shortcut is routed.");
            return;
        }

        var singleKeys = services.GetRequiredService<SingleKeyShortcuts>();
        MainRouter = new ShortcutRouter(window, shortcuts, services.GetRequiredService<LinuxShortcutPlatformService>(),
            new ShortcutRouterContext(
                () => navigation.IsShellReady ? navigation.ActiveMode : null,
                () => navigation.ShellContentViewModel,
                () => navigation.RenderingFrameViewModel,
                details => navigation.Shell?.ViewModel.KeyboardShortcutHookForMode(details) ?? Task.CompletedTask)
            {
                RunFixed = action => RunFixed(window, navigation, action),
                AllowsSingleKeys = focus => singleKeys.IsEnabled && navigation.Shell?.AllowsSingleKeys(focus) == true
            },
            exception => Serilog.Log.Error(exception, "A keyboard shortcut failed."));

        if (window.GetApplication() is not { } application) return;
        if (application.LookupAction(AppActions.Shortcuts) is Gio.SimpleAction action)
        {
            _overviewAction = GtkSignals.OnActivate(action, _ => ShowOverview());
            action.SetEnabled(true);
        }
        var detailed = AppActions.Detailed(AppActions.Shortcuts);
        if (application.GetAccelsForAction(detailed).Length == 0 && LinuxKeymap.Find(AppActions.Shortcuts) is { } overview)
            application.SetAccelsForAction(detailed, overview.Accelerators);
    }

    // Context keys run the shell's handler directly, because a key that does not apply must pass on to the focused
    // widget (Escape in the list clears its selection); single keys that name a global action activate it.
    private static bool RunFixed(Gtk.Window window, GtkNavigationService navigation, string action)
    {
        if (LinuxKeymap.Find(action) is { Scope: LinuxKeyScope.Global })
        {
            if (window.GetApplication() is not { } application || application.LookupAction(action) is not { } found || !found.GetEnabled()) return false;
            application.ActivateAction(action, null);
            return true;
        }
        return navigation.Shell?.TryHandleKeyAction(action) == true;
    }
}
