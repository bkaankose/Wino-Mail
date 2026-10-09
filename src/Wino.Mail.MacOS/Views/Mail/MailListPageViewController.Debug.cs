using AppKit;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Mail;

public sealed partial class MailListPageViewController
{
    /// <summary>
    /// Debug-bridge commands for the mail list parity surfaces (DEBUG builds only):
    /// <c>mail-actionbar</c> (visibility and buttons), <c>mail-undo [run]</c> (undo pack, bar and Cmd+Z state;
    /// <c>run</c> undoes), <c>mail-ctxcats ROW</c> (categories for a mail row with their assigned state),
    /// <c>mail-syncbar</c> (sync-disabled bar and Other link), <c>mail-searchfilters [close]</c> (opens the
    /// filter editor from the header pull-down) and <c>mail-multiselect-pane</c> (bulk action titles).
    /// </summary>
    private void RegisterParityDebugCommands()
    {
#if DEBUG
        MacDebugBridge.Register("mail-actionbar", _ => Task.FromResult(_released ? "released" :
            $"pref={_preferences.IsMailListActionBarEnabled} hidden={_actionBarRow.Hidden} items={ViewModel.ActionItems.Count} selected={ViewModel.SelectedItemsCount}"
            + Environment.NewLine + _actionBar.Dump()));
        MacDebugBridge.Register("mail-undo", args =>
        {
            if (_released) return Task.FromResult("released");
            if (args.Length > 0 && args[0] == "run") UndoMailAction(null);
            var responder = ViewLoaded ? View.Window?.FirstResponder : null;
            return Task.FromResult($"pack={ViewModel.CurrentVisibleUndoMailActionPack?.Title ?? "none"} open={ViewModel.IsUndoMailActionBarOpen} " +
                $"barHidden={_undoBarHost.Hidden} interval={ViewModel.UndoMailActionBarDismissInterval} canUndo={CanUndoMailAction} " +
                $"responds={RespondsToSelector(UndoSelector)} firstResponder={responder?.GetType().Name ?? "none"}");
        });
        MacDebugBridge.Register("mail-ctxcats", async args =>
        {
            if (_released) return "released";
            int ordinal = args.Length > 0 ? int.Parse(args[0]) : 0;
            var row = _entries.Where(static entry => entry.Row is not null).Select(static entry => entry.Row!).ElementAtOrDefault(ordinal);
            if (row is null) return "no such row";
            var targets = Leaves(row);
            var (categories, assigned) = await ViewModel.GetAvailableCategoriesAsync(targets);
            return categories.Count == 0 ? "no categories (item hidden)"
                : string.Join(", ", categories.Select(category => $"{(category.IsFavorite ? "*" : string.Empty)}{category.Name}{(assigned.Contains(category.Id) ? " [x]" : string.Empty)}"));
        });
        MacDebugBridge.Register("mail-syncbar", _ => Task.FromResult(_released ? "released" :
            $"folder={ViewModel.ActiveFolder?.FolderName ?? "none"} syncEnabled={ViewModel.IsFolderSynchronizationEnabled} syncButton={ViewModel.IsSyncButtonVisible} " +
            $"dismissed={_syncBarDismissed} barHidden={_syncBarRow.Hidden} | other={ViewModel.IsOtherInboxUnreadNoticeVisible} otherHidden={_otherInboxRow.Hidden} text='{_otherInboxText.StringValue}'"));
        MacDebugBridge.Register("mail-searchfilters", args =>
        {
            if (_released) return Task.FromResult("released");
            if (args.Length > 0 && args[0] == "close") CloseSearchFilters();
            else if (_filterPopover?.IsShown != true) ShowSearchFilters(_filterButton);
            return Task.FromResult(_filterPopover?.Dump() ?? "closed");
        });
        MacDebugBridge.Register("mail-multiselect-pane", _ => Task.FromResult(_released ? "released" : _multiSelectionView.Dump()));
#endif
    }
}
