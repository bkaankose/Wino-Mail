#if DEBUG
using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary><c>debug-header-state</c> <c>s</c>: writes one <c>key=value</c> line per header property to the given path.</summary>
internal static class HeaderDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-header-state", GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var path = args.Parameter?.GetString(out var _);
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("An output path is required.");
                var shell = services()?.GetService<GtkNavigationService>()?.Shell ?? throw new InvalidOperationException("The shell is not shown.");
                var temporary = path + ".partial";
                File.WriteAllText(temporary, shell.DescribeHeader() + "\n");
                File.Move(temporary, path, overwrite: true);
                Serilog.Log.Information("Debug action: header state written to {Path}.", path);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-header-state failed."); }
        };
        application.AddAction(action);
    }
}
#endif
