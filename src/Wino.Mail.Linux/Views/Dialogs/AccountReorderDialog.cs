using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The account order: the top level (accounts and groups) and, under it, one list per group with accounts. Each list has
/// its own drag group, so a row moves only among its siblings (Windows and macOS rule).
/// </summary>
internal static class AccountReorderDialog
{
#if DEBUG
    private static List<WinoDragList>? _open;
#endif

    /// <summary>Main thread only. True when Save was pressed; <paramref name="items"/> then hold the new order.</summary>
    public static async Task<bool> PresentAsync(Gtk.Widget owner, ObservableCollection<AccountReorderItem> items, IServiceProvider services)
    {
        var form = new FormDialog(Translator.SettingsReorderAccounts_Title, Translator.AccountReorderDialog_Description, Translator.Buttons_Save)
        {
            ContentWidth = 460,
        };
        var pictures = services.GetService<IPictureStorageService>();
        var rows = new Dictionary<AccountReorderItem, WinoDragRow>(ReferenceEqualityComparer.Instance);
        var lists = new List<WinoDragList>();

        WinoDragRow RowFor(AccountReorderItem item)
        {
            if (rows.TryGetValue(item, out var known)) return known;
            var title = item.Title ?? string.Empty;
            var row = Adw.ActionRow.New();
            row.SetUseMarkup(false);
            row.SetTitle(title);
            if (!string.IsNullOrEmpty(item.Subtitle)) row.SetSubtitle(item.Subtitle);
            row.SetTitleLines(1);
            row.SetSubtitleLines(1);
            if (item.Account is { } account && pictures is not null)
                row.AddPrefix(new WinoAccountIcon(28) { Account = MailAccountIconInfoFactory.Create(account, pictures), AccessibleName = title }.Root);
            else
                row.AddPrefix(WinoIcons.Label(item.IsGroup ? item.IconGlyph : WinoIconGlyph.Person, 18));
            return rows[item] = new WinoDragRow(row, title);
        }

        WinoDragList AddList(ObservableCollection<AccountReorderItem> collection, string label, object? tag)
        {
            var list = new WinoDragList(new WinoDragGroup(), label) { ShowsMoveButtons = true, Tag = tag };
            list.Group.Dropped = (_, from, _, to) =>
            {
                if (from < 0 || from >= collection.Count || to < 0 || to >= collection.Count) return;
                collection.Move(from, to);
                list.SetRows(collection.Select(RowFor).ToList());
            };
            list.SetRows(collection.Select(RowFor).ToList());
            form.Add(list.Root);
            form.Own(list);
            lists.Add(list);
            return list;
        }

        AddList(items, Translator.SettingsReorderAccounts_Title, string.Empty);
        foreach (var group in items.Where(item => item.IsGroup && item.Children.Count > 0))
        {
            var heading = Gtk.Label.New(null);
            heading.SetText(group.GroupEntriesAutomationName);
            heading.AddCssClass("heading");
            heading.SetXalign(0);
            heading.SetMarginTop(6);
            form.Add(heading);
            AddList(group.Children, group.GroupEntriesAutomationName, group.Title ?? string.Empty);
        }

#if DEBUG
        _open = lists;
#endif
        try { return await form.PresentAsync(owner); }
        finally
        {
#if DEBUG
            if (ReferenceEquals(_open, lists)) _open = null;
#endif
        }
    }

#if DEBUG
    internal static string? DebugDescribeOpen() => _open is null ? null : string.Join('\n', _open.Select(list => list.Describe())) + "\n";

    internal static bool DebugMove(string listTag, int from, int to)
        => _open?.FirstOrDefault(list => string.Equals(list.Tag as string, listTag, StringComparison.Ordinal)) is { } list && list.Move(from, to);
#endif
}
