#if DEBUG
using System.Globalization;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-maillist-select</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/>: <c>['mode','on'|'off']</c>,
/// <c>['all']</c>, <c>['none']</c>, <c>['rows','0','3',…]</c>, <c>['toggle','2']</c> (the check box path),
/// <c>['thread','4']</c> (a plain click on a head), <c>['chevron','4']</c>, <c>['key','Escape'|'Delete'|'&lt;Control&gt;a'|…]</c>,
/// <c>['state','path']</c> (the multi-selection pane). Each logs <c>Debug action: select …</c> with rows, mails, all and mode.
/// </summary>
internal static class MailSelectionDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-maillist-select", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length == 0) throw new ArgumentException("Expected a command.");
                var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                var result = string.Empty;
                switch (arguments[0])
                {
                    case "mode": view.DebugSetSelectionMode(arguments.Length > 1 && arguments[1] == "on"); break;
                    case "all": view.SelectAllRows(); break;
                    case "none": view.ClearSelection(); break;
                    case "rows": view.DebugSelectPositions([.. arguments.Skip(1).Select(static value => uint.Parse(value, CultureInfo.InvariantCulture))]); break;
                    case "toggle": view.DebugToggle(Position(arguments)); break;
                    case "thread": view.DebugThreadClick(Position(arguments)); break;
                    case "chevron":
                        if (view.EntryAt(Position(arguments)) is { } entry) view.ToggleThread(entry);
                        break;
                    case "key":
                        if (arguments.Length < 2 || !ShortcutKeys.TryParseAccelerator(arguments[1], out var keyval, out var state)) throw new ArgumentException("Expected a key such as <Control>a.");
                        result = " handled=" + view.HandleListKey(keyval, state);
                        if (arguments[1] is "Delete" or "KP_Delete") view.DebugReleaseDelete();
                        break;
                    case "state":
                        if (arguments.Length < 2) throw new ArgumentException("Expected a path.");
                        DebugActions.WriteText(arguments[1], $"{Summary(view)}\npane={view.DebugMultiSelection}\n");
                        break;
                    default: throw new ArgumentException($"Unknown command '{arguments[0]}'.");
                }
                Serilog.Log.Information("Debug action: select {Command}{Result} {Summary}", string.Join(' ', arguments), result, Summary(view));
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-maillist-select failed."); }
        };
        application.AddAction(action);
    }

    private static uint Position(string[] arguments)
        => arguments.Length > 1 ? uint.Parse(arguments[1], CultureInfo.InvariantCulture) : throw new ArgumentException("Expected a position.");

    private static string Summary(MailListView view)
        => $"rows={view.SelectedRowCount} mails={view.ViewModel.SelectedItemsCount} all={view.AllSelectedState?.ToString() ?? "some"} mode={view.IsSelectionMode}";
}
#endif
