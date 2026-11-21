#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Shell;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-sidebar-menu</c> <c>as</c>: <c>['list', row, path]</c> writes <c>path|title|enabled|separator|destructive</c> per
/// entry; <c>['open', row]</c> opens the row's menu; <c>['run', row, entry title or path]</c> runs an entry;
/// <c>['close']</c>; <c>['rename-dialog', folder, display, 'true'|'false']</c> shows the rename dialog and logs the answer.
/// Rows are titles or <c>#index</c> as in <c>debug-sidebar</c>.
/// </summary>
internal static class SidebarMenuDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-sidebar-menu", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length == 0) throw new ArgumentException("debug-sidebar-menu needs an operation.");
                var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
                if (arguments[0] == "rename-dialog")
                {
                    RenameDialog(provider, arguments);
                    return;
                }
                var shell = provider.GetService<GtkNavigationService>()?.Shell ?? throw new InvalidOperationException("The shell is not shown.");
                var menus = shell.SidebarMenus;
                switch (arguments[0])
                {
                    case "list" when arguments.Length >= 3:
                        var text = new StringBuilder();
                        Write(text, Entries(shell, arguments[1]), string.Empty);
                        File.WriteAllText(arguments[2] + ".partial", text.ToString());
                        File.Move(arguments[2] + ".partial", arguments[2], overwrite: true);
                        break;
                    case "open" when arguments.Length >= 2:
                        var item = SidebarDebugActions.Resolve(shell.Sidebar, arguments[1]);
                        var index = shell.Sidebar.IndexOf(item);
                        if (shell.Sidebar.CellOf(item) is null && index >= 0) ListViews.ScrollTo(shell.Sidebar.List, (uint)index);
                        MainLoop.Post(() =>
                        {
                            var opened = shell.Sidebar.CellOf(item) is { } cell && menus.Show(item, 24, cell.Root.GetHeight() / 2.0);
                            Serilog.Log.Information("Debug action: sidebar menu {Result}.", opened ? $"opened with {Entries(shell, arguments[1]).Length} entries" : "no menu");
                        });
                        break;
                    case "run" when arguments.Length >= 3:
                        var entry = Find(Entries(shell, arguments[1]), arguments[2], string.Empty) ?? throw new ArgumentException($"No entry '{arguments[2]}'.");
                        if (!entry.IsEnabled || entry.Run is not { } run)
                        {
                            Serilog.Log.Information("Debug action: sidebar menu entry {Entry} is disabled.", entry.Title);
                            return;
                        }
                        run().ContinueWith(static task => Serilog.Log.Error(task.Exception, "Debug action debug-sidebar-menu run failed."), TaskContinuationOptions.OnlyOnFaulted);
                        break;
                    case "close":
                        menus.Close();
                        break;
                    default:
                        throw new ArgumentException($"Unknown operation or missing arguments: {string.Join(' ', arguments)}.");
                }
                Serilog.Log.Information("Debug action: sidebar menu {Operation} done.", arguments[0]);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-sidebar-menu failed."); }
        };
        application.AddAction(action);
    }

    private static SidebarMenuEntry[] Entries(ShellView shell, string row)
        => ShellSidebarContextMenus.Build(SidebarDebugActions.Resolve(shell.Sidebar, row), shell.SidebarMenuContext);

    private static void Write(StringBuilder text, SidebarMenuEntry[] entries, string prefix)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var path = prefix + index;
            text.Append(path).Append('|').Append(entry.Title).Append('|').Append(entry.IsEnabled).Append('|').Append(entry.IsSeparator).Append('|').Append(entry.IsDestructive).AppendLine();
            if (entry.Children is { } children) Write(text, children, path + ".");
        }
    }

    private static SidebarMenuEntry? Find(SidebarMenuEntry[] entries, string key, string prefix)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var path = prefix + index;
            if (!entry.IsSeparator && (entry.Title == key || entry.Title.TrimEnd('…') == key || path == key)) return entry;
            if (entry.Children is { } children && Find(children, key, path + ".") is { } found) return found;
        }
        return null;
    }

    private static void RenameDialog(IServiceProvider provider, string[] arguments)
    {
        if (arguments.Length < 4) throw new ArgumentException("rename-dialog needs a folder name, a display name and true or false.");
        var dialogs = provider.GetRequiredService<IMailDialogService>();
        _ = Answer();

        async Task Answer()
        {
            try
            {
                var result = await dialogs.ShowRenameFolderDialogAsync(arguments[1], arguments[2], arguments[3] == "true");
                Serilog.Log.Information("Debug action: Rename dialog answered {Answer}.", result is null ? "null" : $"{result.FolderName}|{result.DisplayName}");
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action rename-dialog failed."); }
        }
    }
}
#endif
