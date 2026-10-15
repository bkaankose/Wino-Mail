using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.MailList;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using ActionMode = AndroidX.AppCompat.View.ActionMode;
using IMenu = Android.Views.IMenu;
using IMenuItem = Android.Views.IMenuItem;

using Wino.Mail.Android.Infrastructure;

namespace Wino.Mail.Android.Views.Mail;

public sealed partial class MailListFragment
{
    private const int ActionArchive = 101;
    private const int ActionDelete = 102;
    private const int ActionMove = 103;
    private const int ActionFlag = 104;
    private const int ActionSelectAll = 105;
    private const int ActionMoreBase = 200;

    private ActionMode? _actionMode;
    private TextView? _actionModeTitle;
    private List<(MailOperation Operation, bool Enabled)> _moreOperations = [];

    // ---- Swipe (canvas Components: leading Archive, trailing Delete) ----

    private void AttachSwipe(Context context)
    {
        if (_list is null || _adapter is null) return;
        var helper = WinoMailSwipeHelper.Attach(_list, _adapter, (row, leading) =>
        {
            if (_actionMode is not null || Leaves(row).Count == 0) return null;
            if (leading) return ViewModel?.IsArchiveSpecialFolder == true ? MailOperation.UnArchive : MailOperation.Archive;
            return MailOperation.SoftDelete;
        }, (row, operation) => RunOperation(operation, Leaves(row), row.SourceItem as MailItemViewModel));
        Bindings.Own(new ActionDisposable(() => helper.AttachToRecyclerView(null)));
    }

    private static IReadOnlyList<MailItemViewModel> Leaves(MailListRow row)
        => (row.IsThreadHead ? row.LeafItems : new[] { row.SourceItem }).OfType<MailItemViewModel>().ToArray();

    /// <summary>Runs an operation on explicit targets (swipe, the action mode), like the Mac and iOS lists.</summary>
    private void RunOperation(MailOperation operation, IReadOnlyList<MailItemViewModel> items, MailItemViewModel? composeTarget)
    {
        if (items.Count == 0 || ViewModel is not { } viewModel) return;
        switch (operation)
        {
            case MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward:
                Observe(viewModel.CreateDraftFromMailAsync(composeTarget ?? items[0], operation));
                return;
            case MailOperation.RetryDraftUpload:
                Observe(viewModel.RetryDraftUploadAsync(composeTarget ?? items[0]));
                return;
            case MailOperation.Move:
                ShowMove(items.Select(static item => item.MailCopy).ToArray());
                return;
            default:
                Observe(viewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(operation, items.Select(static item => item.MailCopy))));
                return;
        }
    }

    // ---- Multi-select: the contextual action mode (canvas decision A8) ----

    /// <summary>Starts multi-select, with <paramref name="row"/> selected when it came from a long press.</summary>
    private void StartSelectionMode(MailListRow? row)
    {
        if (_adapter is null || ViewModel is not { } viewModel || Activity is not AppCompatActivity activity) return;
        if (_actionMode is null)
        {
            _adapter.ClearSelection();
            viewModel.IsMultiSelectionModeEnabled = true;
            _adapter.IsSelectionMode = true;
            _actionMode = activity.StartSupportActionMode(new SelectionCallback(this));
            if (_actionMode is not null && IsCompact) _actionMode.CustomView = _actionModeTitle = CompactSelectionTitle(activity);
        }
        if (row is not null) _adapter.SetSelected(row, true);
        UpdateActionMode();
    }

    /// <summary>Leaves multi-select (Done, back, a destructive action) and drops the selection.</summary>
    private void EndSelectionMode()
    {
        var mode = _actionMode;
        if (mode is null) return;
        _actionMode = null;
        _actionModeTitle = null;
        mode.Finish();
        SelectionModeEnded();
    }

    private void SelectionModeEnded()
    {
        if (_adapter is not null)
        {
            _adapter.IsSelectionMode = false;
            _adapter.ClearSelection();
        }
        if (ViewModel is { IsMultiSelectionModeEnabled: true } viewModel) viewModel.IsMultiSelectionModeEnabled = false;
    }

    private void UpdateActionMode()
    {
        if (_actionMode is not { } mode || ViewModel is not { } viewModel) return;
        int count = viewModel.SelectedItemsCount;
        string title = count > 0 ? string.Format(Translator.MailsSelected, count) : Translator.NoMailSelected;
        if (_actionModeTitle is { } compact)
        {
            // Phones: the count alone (the Material contextual app bar), so Archive, Delete and Move keep their room;
            // TalkBack still reads the shared sentence.
            compact.Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            compact.ContentDescription = title;
        }
        else mode.Title = title;
        mode.Invalidate();
    }

    /// <summary>The compact action mode title: one line in the top app bar title style.</summary>
    private static TextView CompactSelectionTitle(Context context)
    {
        var title = new TextView(context);
        AndroidX.Core.Widget.TextViewCompat.SetTextAppearance(title, Resource.Style.TextAppearance_WinoKit_TopAppBarTitle);
        title.SetSingleLine(true);
        title.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        title.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        return title;
    }

    private void PrepareActionMenu(IMenu menu)
    {
        if (View?.Context is not { } context || ViewModel is not { } viewModel) return;
        menu.Clear();
        var selected = viewModel.SelectedItems.ToArray();
        bool any = selected.Length > 0;
        var tint = WinoColors.SecondaryText(context);
        double glyph = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
        // Canvas Material pass InboxDark: Archive, Delete and Move on the bar, the rest in the overflow.
        void Add(int id, int order, string title, WinoIconGlyph icon, ShowAsAction show)
        {
            var item = menu.Add(0, id, order, title);
            item?.SetIcon(WinoIcons.Drawable(context, icon, glyph, tint));
            item?.SetShowAsAction(show);
            item?.SetEnabled(any);
        }
        var archive = viewModel.IsArchiveSpecialFolder ? MailOperation.UnArchive : MailOperation.Archive;
        Add(ActionArchive, 0, MailOperationDisplay.GetTitle(archive), MailOperationDisplay.GetGlyph(archive), ShowAsAction.Always);
        Add(ActionDelete, 1, Translator.MailOperation_Delete, WinoIconGlyph.Delete, ShowAsAction.Always);
        Add(ActionMove, 2, Translator.MailOperation_Move, WinoIconGlyph.Move, ShowAsAction.Always);
        bool allFlagged = any && selected.All(static item => item.IsFlagged);
        Add(ActionFlag, 3, allFlagged ? MailOperationDisplay.GetTitle(MailOperation.ClearFlag) : Translator.MailOperation_Flag, allFlagged ? WinoIconGlyph.ClearFlag : WinoIconGlyph.Flag, ShowAsAction.IfRoom);
        menu.Add(0, ActionSelectAll, 4, viewModel.IsAllItemsSelected && any ? Translator.Buttons_UnselectAll : Translator.MacOSMenu_SelectAll);

        // The rest of the shared actions (Windows MailContextFlyoutBuilder) in the overflow, then Pin.
        _moreOperations = [];
        if (!any) return;
        foreach (var available in viewModel.GetAvailableMailActions(selected))
        {
            if (available.Operation is MailOperation.Seperator or MailOperation.Archive or MailOperation.UnArchive or MailOperation.SoftDelete
                or MailOperation.SetFlag or MailOperation.ClearFlag or MailOperation.Move) continue;
            var title = MailOperationDisplay.GetTitle(available.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            _moreOperations.Add((available.Operation, available.IsEnabled));
            menu.Add(0, ActionMoreBase + _moreOperations.Count - 1, 10 + _moreOperations.Count, title)?.SetEnabled(available.IsEnabled);
        }
        bool allPinned = selected.All(static item => item.MailCopy.IsPinned);
        menu.Add(0, ActionMoreBase + 99, 200, allPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin);
    }

    private bool ActionClicked(IMenuItem item)
    {
        if (ViewModel is not { } viewModel || _adapter is null) return false;
        var selected = viewModel.SelectedItems.ToArray();
        switch (item.ItemId)
        {
            case ActionArchive:
                RunAndClose(viewModel.IsArchiveSpecialFolder ? MailOperation.UnArchive : MailOperation.Archive, selected);
                return true;
            case ActionDelete:
                RunAndClose(MailOperation.SoftDelete, selected);
                return true;
            case ActionMove:
                ShowMove(selected.Select(static mail => mail.MailCopy).ToArray());
                return true;
            case ActionFlag:
                RunOperation(selected.All(static mail => mail.IsFlagged) ? MailOperation.ClearFlag : MailOperation.SetFlag, selected, null);
                return true;
            case ActionSelectAll:
                if (viewModel.IsAllItemsSelected && selected.Length > 0) _adapter.ClearSelection();
                else _adapter.SelectAll();
                return true;
            case ActionMoreBase + 99:
                Observe(viewModel.ChangePinnedStatusAsync(selected, !selected.All(static mail => mail.MailCopy.IsPinned)));
                return true;
            case >= ActionMoreBase when item.ItemId - ActionMoreBase < _moreOperations.Count:
                RunOperation(_moreOperations[item.ItemId - ActionMoreBase].Operation, selected, selected.FirstOrDefault());
                return true;
        }
        return false;
    }

    private void RunAndClose(MailOperation operation, IReadOnlyList<MailItemViewModel> items)
    {
        RunOperation(operation, items, null);
        EndSelectionMode();
    }

    private sealed class SelectionCallback(MailListFragment owner) : Java.Lang.Object, ActionMode.ICallback
    {
        public bool OnCreateActionMode(ActionMode mode, IMenu menu)
        {
            owner.PrepareActionMenu(menu);
            return true;
        }

        public bool OnPrepareActionMode(ActionMode mode, IMenu menu)
        {
            owner.PrepareActionMenu(menu);
            return true;
        }

        public bool OnActionItemClicked(ActionMode mode, IMenuItem item) => owner.ActionClicked(item);

        public void OnDestroyActionMode(ActionMode mode)
        {
            // Back or Done closed the mode.
            if (!ReferenceEquals(owner._actionMode, mode)) return;
            owner._actionMode = null;
            owner._actionModeTitle = null;
            owner.SelectionModeEnded();
        }
    }

    // ---- Move (bottom sheet on compact, dialog on wider windows; canvas board MoveSheet) ----

    /// <summary>Opens the Move picker for <paramref name="mails"/>; mixed accounts use the shared folder picker.</summary>
    private void ShowMove(IReadOnlyList<MailCopy> mails)
    {
        if (mails.Count == 0 || ViewModel is not { } viewModel) return;
        if (!MailMovePresenter.TryShow(this, viewModel, mails, IsCompact, moved: EndSelectionMode))
            Observe(viewModel.ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move, mails)));
    }
}
