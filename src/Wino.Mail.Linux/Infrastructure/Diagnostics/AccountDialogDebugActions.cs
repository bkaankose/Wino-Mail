#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-account-dialog</c> (<c>s</c>): <c>reorder</c>, <c>alias</c>, <c>category</c>, <c>category-edit</c> open that
/// dialog and log the answer. <c>debug-reorder</c> (<c>as</c>): <c>['state', path]</c>, <c>['move', listTag, from, to]</c>.
/// <c>debug-alias-fill</c> (<c>as</c> [address, replyTo, 'confirm'|'']). <c>debug-category-dialog</c> (<c>as</c>):
/// <c>['state', path]</c>, <c>['set', name, swatchIndex, 'confirm'|'']</c>. <c>debug-draglist</c> opens a window with two
/// lists that share a group; <c>debug-draglist-move</c> (<c>as</c> [sourceList, from, targetList, to]) and
/// <c>debug-draglist-state</c> (<c>s</c> path).
/// </summary>
internal static class AccountDialogDebugActions
{
    private static DragListWindow? _window;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var open = Gio.SimpleAction.New("debug-account-dialog", GLib.VariantType.New("s"));
        open.OnActivate += (sender, args) => Observe(OpenAsync(services, args.Parameter?.GetString(out _) ?? string.Empty));
        application.AddAction(open);

        var reorder = Gio.SimpleAction.New("debug-reorder", GLib.VariantType.New("as"));
        reorder.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            switch (parts)
            {
                case ["state", var path]:
                    DebugActions.WriteText(path, AccountReorderDialog.DebugDescribeOpen() ?? "no reorder dialog open\n");
                    break;
                case ["move", var tag, var from, var to]:
                    Serilog.Log.Information("Debug action: reorder move {Result}.", AccountReorderDialog.DebugMove(tag, int.Parse(from), int.Parse(to)));
                    break;
            }
        };
        application.AddAction(reorder);

        var alias = Gio.SimpleAction.New("debug-alias-fill", GLib.VariantType.New("as"));
        alias.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            Serilog.Log.Information("Debug action: alias fill {Result}.", CreateAliasDialog.DebugFill(
                parts.Length > 0 ? parts[0] : string.Empty, parts.Length > 1 ? parts[1] : string.Empty, parts.Length > 2 && parts[2] == "confirm"));
        };
        application.AddAction(alias);

        var category = Gio.SimpleAction.New("debug-category-dialog", GLib.VariantType.New("as"));
        category.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            switch (parts)
            {
                case ["state", var path]:
                    DebugActions.WriteText(path, EditMailCategoryDialog.DebugDescribeOpen() ?? "no category dialog open\n");
                    break;
                case ["set", var name, var swatch, var confirm]:
                    Serilog.Log.Information("Debug action: category set {Result}.", EditMailCategoryDialog.DebugSet(name, int.Parse(swatch), confirm == "confirm"));
                    break;
            }
        };
        application.AddAction(category);

        var dragList = Gio.SimpleAction.New("debug-draglist", null);
        dragList.OnActivate += (sender, args) =>
        {
            _window?.Close();
            _window = new DragListWindow(application);
        };
        application.AddAction(dragList);

        var dragMove = Gio.SimpleAction.New("debug-draglist-move", GLib.VariantType.New("as"));
        dragMove.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            if (_window is null || parts.Length < 4) return;
            Serilog.Log.Information("Debug action: drag list move {Result}.", _window.Move(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3])));
        };
        application.AddAction(dragMove);

        var dragState = Gio.SimpleAction.New("debug-draglist-state", GLib.VariantType.New("s"));
        dragState.OnActivate += (sender, args) =>
            DebugActions.WriteText(args.Parameter?.GetString(out _) ?? string.Empty, _window?.Describe() ?? "no drag list window\n");
        application.AddAction(dragState);

        var deny = Gio.SimpleAction.New("debug-draglist-deny", GLib.VariantType.New("b"));
        deny.OnActivate += (sender, args) => _window?.Deny(args.Parameter?.GetBoolean() ?? false);
        application.AddAction(deny);
    }

    private static async Task OpenAsync(Func<IServiceProvider?> services, string kind)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var dialogs = provider.GetRequiredService<IMailDialogService>();
        string answer;
        switch (kind)
        {
            case "reorder":
                await dialogs.ShowAccountReorderDialogAsync([]);
                answer = "done";
                break;
            case "alias":
            {
                var alias = (await dialogs.ShowCreateAccountAliasDialogAsync()).CreatedAccountAlias;
                answer = alias is null ? "null" : $"{alias.AliasAddress} replyTo={alias.ReplyToAddress} IsPrimary={alias.IsPrimary} Source={alias.Source}";
                break;
            }
            case "category":
            case "category-edit":
            {
                var option = MailCategoryPalette.DefaultOptions[4];
                var existing = kind == "category" ? null : new MailCategory { Id = Guid.NewGuid(), Name = "Lab category", BackgroundColorHex = option.BackgroundColorHex, TextColorHex = option.TextColorHex };
                var result = await dialogs.ShowEditMailCategoryDialogAsync(existing);
                answer = result is null ? "null" : $"{result.Name} {result.BackgroundColorHex}/{result.TextColorHex}";
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-account-dialog kind '{kind}'.");
        }
        Serilog.Log.Information("Debug action: account dialog {Kind} answered {Answer}", kind, answer);
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-account-dialog failed."); }
    }

    private sealed class DragListWindow
    {
        private readonly Adw.Window _window;
        private readonly WinoDragList[] _lists;
        private readonly List<string>[] _items = [["Row 1", "Row 2", "Row 3"], []];
        private readonly Dictionary<string, WinoDragRow> _rows = new(StringComparer.Ordinal);
        private bool _deny;

        public DragListWindow(Adw.Application application)
        {
            var group = new WinoDragGroup { CanDrop = (_, _) => !_deny };
            _lists = [new WinoDragList(group, "First list") { ShowsMoveButtons = true, Tag = "0" }, new WinoDragList(group, "Second list") { ShowsMoveButtons = true, Tag = "1" }];
            group.Dropped = (source, from, target, to) =>
            {
                var sourceItems = _items[Array.IndexOf(_lists, source)];
                var targetItems = _items[Array.IndexOf(_lists, target)];
                var item = sourceItems[from];
                sourceItems.RemoveAt(from);
                targetItems.Insert(Math.Clamp(to, 0, targetItems.Count), item);
                Refresh();
                Serilog.Log.Information("Debug action: drag list: {Lists}", Describe().Replace('\n', ' '));
            };
            var empty = Adw.ActionRow.New();
            empty.SetTitle("Drop rows here");
            empty.SetActivatable(false);
            _lists[1].SetEmptyRow(empty);

            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
            box.SetMarginTop(18);
            box.SetMarginBottom(18);
            box.SetMarginStart(18);
            box.SetMarginEnd(18);
            box.Append(_lists[0].Root);
            box.Append(_lists[1].Root);
            var view = Adw.ToolbarView.New();
            view.AddTopBar(Adw.HeaderBar.New());
            view.SetContent(box);
            _window = Adw.Window.New();
            _window.SetTitle("Wino drag lists");
            _window.SetDefaultSize(420, 420);
            _window.SetContent(view);
            _window.SetApplication(application);
            Refresh();
            _window.Present();
        }

        public bool Move(int source, int from, int target, int to) => _lists[source].MoveTo(from, _lists[target], to);

        public void Deny(bool deny) => _deny = deny;

        public string Describe() => string.Join('\n', _lists.Select(list => list.Describe())) + "\n";

        public void Close()
        {
            foreach (var list in _lists) list.Dispose();
            _window.Destroy();
        }

        private void Refresh()
        {
            for (var index = 0; index < _lists.Length; index++)
                _lists[index].SetRows(_items[index].Select(RowFor).ToList());
        }

        private WinoDragRow RowFor(string name)
        {
            if (_rows.TryGetValue(name, out var known)) return known;
            var row = Adw.ActionRow.New();
            row.SetTitle(name);
            return _rows[name] = new WinoDragRow(row, name);
        }
    }
}
#endif
