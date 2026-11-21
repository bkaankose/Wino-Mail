using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>The mode shortcuts Ctrl+1 to Ctrl+4 as application actions <c>app.mode-*</c>; they exist in Release too.</summary>
internal static class ShellModeActions
{
    public static string Name(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => "mode-calendar",
        WinoApplicationMode.Contacts => "mode-contacts",
        WinoApplicationMode.Tasks => "mode-tasks",
        _ => "mode-mail"
    };

    // Actions live as long as the application, so a plain handler cannot keep a view alive.
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        for (var index = 0; index < AppModeVisibility.SwitchableModes.Length; index++)
        {
            var mode = AppModeVisibility.SwitchableModes[index];
            var action = Gio.SimpleAction.New(Name(mode), null);
            action.OnActivate += (_, _) =>
            {
                try
                {
                    if (services() is not { } provider) return;
                    if (provider.GetService<IPreferencesService>()?.HiddenApplicationModes?.Contains(mode) == true) return;
                    if (provider.GetService<GtkNavigationService>()?.Shell?.VisibleModes is { } visible && !visible.Contains(mode)) return;
                    provider.GetService<INavigationService>()?.ChangeApplicationMode(mode);
                }
                catch (Exception exception) { Serilog.Log.Error(exception, "The mode action {Mode} failed.", mode); }
            };
            application.AddAction(action);
            application.SetAccelsForAction("app." + Name(mode), [$"<Control>{index + 1}"]);
        }
    }
}
