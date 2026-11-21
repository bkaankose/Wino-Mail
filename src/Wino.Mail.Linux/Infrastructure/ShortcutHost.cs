using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux.Services;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Owns the main window's <see cref="ShortcutRouter"/> and the Keyboard shortcuts overview (<c>app.shortcuts</c>,
/// Ctrl+?). Other windows construct their own router with <see cref="ShortcutRouterContext.IsPopOut"/>.
/// </summary>
internal sealed class ShortcutHost(IServiceProvider services, IDispatcher dispatcher) : IApplicationLifecycleHook
{
    public const string OverviewAccelerator = "<Control>question";

    private IDisposable? _overviewAction;

    public ShortcutRouter? MainRouter { get; private set; }

    public async Task ServicesReadyAsync()
    {
        var shortcuts = services.GetRequiredService<IKeyboardShortcutService>();
        await shortcuts.InitializeAsync();
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

    public void ShowOverview()
    {
        var navigation = services.GetRequiredService<GtkNavigationService>();
        if (navigation.MainWindow?.Window is not { } window || window.GetApplication() is not { } application) return;
        ShortcutsOverview.Present(window, services.GetRequiredService<IKeyboardShortcutService>(), application);
    }

    private void Attach(IKeyboardShortcutService shortcuts)
    {
        var navigation = services.GetRequiredService<GtkNavigationService>();
        if (navigation.MainWindow?.Window is not { } window)
        {
            Serilog.Log.Warning("Keyboard shortcuts: the main window does not exist; no shortcut is routed.");
            return;
        }

        MainRouter = new ShortcutRouter(window, shortcuts, services.GetRequiredService<LinuxShortcutPlatformService>(),
            new ShortcutRouterContext(
                () => navigation.IsShellReady ? navigation.ActiveMode : null,
                () => navigation.ShellContentViewModel,
                () => navigation.RenderingFrameViewModel,
                details => navigation.Shell?.ViewModel.KeyboardShortcutHookForMode(details) ?? Task.CompletedTask),
            exception => Serilog.Log.Error(exception, "A keyboard shortcut failed."));

        if (window.GetApplication() is not { } application) return;
        if (application.LookupAction(AppActions.Shortcuts) is Gio.SimpleAction action)
        {
            _overviewAction = GtkSignals.OnActivate(action, _ => ShowOverview());
            action.SetEnabled(true);
        }
        var detailed = AppActions.Detailed(AppActions.Shortcuts);
        if (application.GetAccelsForAction(detailed).Length == 0) application.SetAccelsForAction(detailed, [OverviewAccelerator]);
    }
}
