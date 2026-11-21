#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views;
using Wino.Mail.Linux.Views.Shell;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the shell layout: <c>debug-sidebar-width</c> <c>i</c> and <c>debug-list-width</c> <c>i</c> (the
/// same path as the end of a drag), <c>debug-toggle-sidebar</c>, and <c>debug-shell-state</c> <c>s</c>, which writes
/// one <c>key=value</c> line per layout property to the given path.
/// </summary>
internal static class ShellDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services, Func<MainWindow?> window)
    {
        ShellLayout Layout() => services()?.GetService<GtkNavigationService>()?.Shell?.Layout
            ?? throw new InvalidOperationException("The shell is not shown.");

        Add(application, "debug-sidebar-width", "i", parameter => Layout().SetSidebarWidth(parameter.GetInt32(), persist: true));
        Add(application, "debug-list-width", "i", parameter => Layout().SetListWidth(parameter.GetInt32(), persist: true));
        Add(application, "debug-toggle-sidebar", null, _ => Layout().ToggleSidebar());
        Add(application, "debug-shell-state", "s", parameter =>
        {
            var path = parameter.GetString(out _);
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("An output path is required.");
            var layout = Layout();
            var text = new StringBuilder();
            if (window() is { } main)
            {
                var placement = main.CapturePlacement();
                text.Append("window=").Append(placement.Width).Append('x').Append(placement.Height).AppendLine();
                text.Append("windowMeasured=").Append(main.Window.GetWidth()).Append('x').Append(main.Window.GetHeight()).AppendLine();
                text.Append("maximized=").Append(placement.IsMaximized).AppendLine();
                var focus = main.Window.GetFocus();
                text.Append("focus=").Append(focus is null ? "none" : $"{focus.GetType().Name}:{focus.GetCssName()}:{focus.GetParent()?.GetType().Name}").AppendLine();
            }
            text.Append("WidthClass=").Append(layout.WidthClass).AppendLine();
            text.Append("IsSidebarOverlay=").Append(layout.IsSidebarOverlay).AppendLine();
            text.Append("IsSidebarVisible=").Append(layout.IsSidebarVisible).AppendLine();
            text.Append("SidebarWidth=").Append(layout.SidebarWidth).AppendLine();
            text.Append("SidebarMeasured=").Append(layout.MeasuredSidebarWidth).AppendLine();
            text.Append("IsStacked=").Append(layout.IsStacked).AppendLine();
            text.Append("ShowsDetail=").Append(layout.ShowsDetail).AppendLine();
            text.Append("ListWidth=").Append(layout.ListWidth).AppendLine();
            text.Append("ListMeasured=").Append(layout.MeasuredListWidth).AppendLine();
            text.Append("ReaderMeasured=").Append(layout.MeasuredReaderWidth).AppendLine();
            var temporary = path + ".partial";
            File.WriteAllText(temporary, text.ToString());
            File.Move(temporary, path, overwrite: true);
            Serilog.Log.Information("Debug action: shell state written to {Path}.", path);
        });
    }

    private static void Add(Adw.Application application, string name, string? parameterType, Action<GLib.Variant> handler)
    {
        var action = Gio.SimpleAction.New(name, parameterType is null ? null : GLib.VariantType.New(parameterType));
        action.OnActivate += (_, args) =>
        {
            try
            {
                if (parameterType is null) handler(GLib.Variant.NewBoolean(false));
                else if (args.Parameter is { } parameter) handler(parameter);
                else throw new ArgumentException($"{name} needs a parameter of type {parameterType}.");
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
        };
        application.AddAction(action);
    }
}
#endif
