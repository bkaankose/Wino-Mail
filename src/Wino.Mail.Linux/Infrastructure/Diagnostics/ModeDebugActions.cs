#if DEBUG
using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary><c>debug-mode-state</c> <c>s</c>: writes the active, current, visible and available modes and the first rows of the mode menu.</summary>
internal static class ModeDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-mode-state", GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var path = args.Parameter?.GetString(out var _);
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("An output path is required.");
                var shell = services()?.GetService<GtkNavigationService>()?.Shell ?? throw new InvalidOperationException("The shell is not shown.");
                var temporary = path + ".partial";
                File.WriteAllText(temporary, shell.DescribeModes());
                File.Move(temporary, path, overwrite: true);
                Serilog.Log.Information("Debug action: mode state written to {Path}.", path);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mode-state failed."); }
        };
        application.AddAction(action);
    }
}
#endif
