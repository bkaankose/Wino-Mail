using UIKit;
using Wino.Core.Domain.Enums;
#if DEBUG
using Wino.Mail.UIKit.Infrastructure;
#endif

namespace Wino.Mail.UIKit.Phone.Mail;

public sealed partial class MailListPageViewController
{
    /// <summary>
    /// Debug bridge commands for the Simulator: selectmail N (opens row N as a tap does) · mailop OPERATION ·
    /// mailinfo · editmode on|off · selectall · search TEXT · searchclear · filters · movesheet · readerbar ·
    /// expand N · listbar.
    /// </summary>
    private void RegisterDebugCommands()
    {
#if DEBUG
        UIKitDebugBridge.Register("selectmail", args =>
        {
            if (_released) return Task.FromResult("released");
            var rows = DisplayedRows().ToArray();
            int ordinal = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            if (ordinal < 0 || ordinal >= rows.Length) return Task.FromResult("no such row");
            var indexPath = rows[ordinal].IndexPath;
            if (!_selectionMode) foreach (var other in _table.IndexPathsForSelectedRows ?? []) _table.DeselectRow(other, false);
            _table.SelectRow(indexPath, false, UITableViewScrollPosition.Middle);
            SyncSelectionFromTable();
            return Task.FromResult("ok " + SelectionKey(rows[ordinal].Row));
        });
        UIKitDebugBridge.Register("mailop", args =>
        {
            if (_released) return Task.FromResult("released");
            if (ViewModel.SelectedItemsCount == 0) return Task.FromResult("no selection");
            RunOnSelection(Enum.Parse<MailOperation>(args[0], true));
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("mailinfo", _ => Task.FromResult(_released ? "released" :
            $"rows={_rowsByKey.Count} sections={_sectionTitles.Count} selected={ViewModel.SelectedItemsCount} edit={_selectionMode} " +
            $"covered={IsCoveredByReader} folder='{ViewModel.ActiveFolder?.FolderName}' pivots={ViewModel.PivotFolders.Count} " +
            string.Join(" | ", ViewModel.SelectedItems.Take(3).Select(item => $"'{item.Subject}' read={item.IsRead} flagged={item.IsFlagged}"))));
        UIKitDebugBridge.Register("editmode", args =>
        {
            ViewModel.IsMultiSelectionModeEnabled = args.Length == 0 || args[0].StartsWith("on", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("selectall", _ => { SelectAllRows(); return Task.FromResult("ok " + ViewModel.SelectedItemsCount); });
        UIKitDebugBridge.Register("search", async args =>
        {
            var text = string.Join(' ', args);
            _search.Active = true;
            SetSearchText(text);
            await SearchSubmittedAsync(text);
            return "ok " + ViewModel.MailCollection.Count;
        });
        UIKitDebugBridge.Register("searchclear", async _ =>
        {
            _search.Active = false;
            await SearchClearedAsync();
            return "ok";
        });
        UIKitDebugBridge.Register("filters", _ => { ShowSearchFilters(); return Task.FromResult("ok"); });
        UIKitDebugBridge.Register("movesheet", _ => { ShowMoveSheet(ViewModel.SelectedItems.ToArray()); return Task.FromResult("ok"); });
        UIKitDebugBridge.Register("readerbar", _ =>
        {
            var top = NavigationController?.TopViewController;
            var items = top?.ToolbarItems ?? [];
            var states = items.SelectMany(item => item.CustomView?.Subviews.OfType<UIButton>().Select(button => (Label: button.AccessibilityLabel, button.Enabled))
                    ?? [(item.AccessibilityLabel, item.Enabled)])
                .Where(state => state.Label is not null)
                .Select(state => $"{state.Label}={(state.Enabled ? "on" : "off")}");
            return Task.FromResult($"top={top?.GetType().Name} toolbarHidden={NavigationController?.ToolbarHidden} " + string.Join(", ", states));
        });
        UIKitDebugBridge.Register("expand", args =>
        {
            var rows = DisplayedRows().Select(entry => entry.Row).Where(row => row.IsThreadHead).ToArray();
            int ordinal = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            if (ordinal < 0 || ordinal >= rows.Length) return Task.FromResult("no thread");
            ToggleThread(rows[ordinal]);
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("listbar", _ =>
        {
            ViewModel.BarSeverity = InfoBarMessageType.Success;
            ViewModel.BarTitle = ViewModel.ActiveFolder?.FolderName ?? string.Empty;
            ViewModel.BarMessage = "up to date";
            ViewModel.IsBarOpen = true;
            return Task.FromResult("ok");
        });
#endif
    }
}
