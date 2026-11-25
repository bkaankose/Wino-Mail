#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-sidebar</c> <c>as</c>: <c>['rows', path]</c> writes one line per row
/// (<c>index|type|depth|title|unread|expanded|selectable|selected|enabled|bound</c>); <c>['invoke', row]</c>,
/// <c>['toggle', row]</c> with a row title or <c>#index</c>; <c>['sync', row]</c> plays a synchronization on an account row.
/// </summary>
internal static class SidebarDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-sidebar", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length < 2) throw new ArgumentException("debug-sidebar needs an operation and an argument.");
                var shell = services()?.GetService<GtkNavigationService>()?.Shell ?? throw new InvalidOperationException("The shell is not shown.");
                var sidebar = shell.Sidebar;
                switch (arguments[0])
                {
                    case "rows":
                        Write(arguments[1], Rows(sidebar));
                        break;
                    case "invoke":
                        sidebar.InvokeAsync(Resolve(sidebar, arguments[1])).ContinueWith(static task => Serilog.Log.Error(task.Exception, "Debug action debug-sidebar invoke failed."), TaskContinuationOptions.OnlyOnFaulted);
                        break;
                    case "toggle":
                        sidebar.ToggleExpansion(Resolve(sidebar, arguments[1]));
                        break;
                    case "sync":
                        if (Resolve(sidebar, arguments[1]) is not AccountMenuItem account) throw new ArgumentException("Not an account row.");
                        PlaySync(account);
                        break;
                    default:
                        throw new ArgumentException($"Unknown operation {arguments[0]}.");
                }
                Serilog.Log.Information("Debug action: sidebar {Operation} {Argument} done.", arguments[0], arguments[1]);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-sidebar failed."); }
        };
        application.AddAction(action);
    }

    internal static IMenuItem Resolve(ShellSidebarView sidebar, string row)
    {
        var rows = sidebar.Rows;
        if (row.StartsWith('#') && int.TryParse(row[1..], out var index) && index >= 0 && index < rows.Count) return rows[index];
        foreach (var item in rows)
        {
            if (Title(sidebar, item) == row) return item;
        }
        throw new ArgumentException($"No sidebar row '{row}'.");
    }

    private static string Title(ShellSidebarView sidebar, IMenuItem item)
        => sidebar.Describe(item).Content?.Title ?? string.Empty;

    private static string Rows(ShellSidebarView sidebar)
    {
        var text = new StringBuilder();
        var rows = sidebar.Rows;
        for (var index = 0; index < rows.Count; index++)
        {
            var item = rows[index];
            var shown = sidebar.Describe(item);
            var content = shown.Content;
            text.Append(index).Append('|').Append(item.GetType().Name).Append('|').Append(content?.Indent ?? 0).Append('|')
                .Append(content?.Title).Append('|').Append(content?.UnreadCount ?? 0).Append('|')
                .Append(content?.IsExpanded is { } expanded ? expanded.ToString().ToLowerInvariant() : "-").Append('|')
                .Append(ShellMenuItemRules.IsInteractive(item) && ShellMenuItemRules.IsEnabled(item)).Append('|')
                .Append(shown.Extras?.IsSelected ?? false).Append('|')
                .Append(content?.IsEnabled ?? true).Append('|')
                .Append(sidebar.CellOf(item) is not null);
            if (item is IBaseFolderMenuItem folder && folder.HandlingFolders.FirstOrDefault() is { } handled)
                text.Append("|scope=").Append(handled.Scope).Append("|container=").Append(handled.IsContainer).Append("|owner=").Append(handled.IsSharedOwnerNode)
                    .Append("|rights=").Append(handled.MyRights).Append("|selectable=").Append(folder.IsSelectable).Append("|moveTarget=").Append(folder.IsMoveTarget)
                    .Append("|qualified=").Append(handled.QualifiedDisplayName).Append("|glyph=").Append(content?.Glyph);
            text.AppendLine();
        }
        return text.ToString();
    }

    private static void PlaySync(AccountMenuItem account)
    {
        account.TotalItemsToSync = 0;
        account.IsSynchronizationInProgress = true;
        var step = 0;
        MainLoop.Delay(2000, () =>
        {
            account.TotalItemsToSync = 100;
            account.RemainingItemsToSync = 100;
            Step();
        });

        void Step()
        {
            if (step > 5)
            {
                account.IsSynchronizationInProgress = false;
                account.TotalItemsToSync = 0;
                account.RemainingItemsToSync = 0;
                return;
            }
            account.RemainingItemsToSync = 100 - step * 20;
            step++;
            MainLoop.Delay(600, Step);
        }
    }

    private static void Write(string path, string text)
    {
        File.WriteAllText(path + ".partial", text);
        File.Move(path + ".partial", path, overwrite: true);
    }
}
#endif
