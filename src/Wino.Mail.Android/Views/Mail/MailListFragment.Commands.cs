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

using Wino.Mail.Android.Infrastructure;

namespace Wino.Mail.Android.Views.Mail;

public sealed partial class MailListFragment
{
    private const int MenuSelectAll = 105;

    private bool _selecting;
    private LinearLayout? _selectionToolbar;

    // ---- Swipe (the user's swipe preferences, as on Windows, iPad and the Mac) ----

    private void AttachSwipe(Context context)
    {
        if (_list is null || _adapter is null) return;
        var helper = WinoMailSwipeHelper.Attach(_list, _adapter, SwipeOperation,
            (row, operation) => RunOperation(operation, Leaves(row), row.SourceItem as MailItemViewModel));
        Bindings.Own(new ActionDisposable(() => helper.AttachToRecyclerView(null)));
    }

    /// <summary>
    /// A rightward (leading) swipe runs the "right swipe" preference and a leftward one the "left swipe" preference, read
    /// at swipe time so a settings change applies at once. Flag and read state toggle against the row; Archive in the
    /// archive folder is Unarchive. None or disabled swipe actions turn that side off.
    /// </summary>
    private MailOperation? SwipeOperation(MailListRow row, bool leading)
    {
        if (_selecting || _preferences is not { IsSwipeActionsEnabled: true } preferences) return null;
        var items = Leaves(row);
        if (items.Count == 0) return null;
        var operation = leading ? preferences.RightSwipeOperation : preferences.LeftSwipeOperation;
        return operation switch
        {
            MailOperation.None => null,
            MailOperation.SetFlag or MailOperation.ClearFlag => items.All(static item => item.IsFlagged) ? MailOperation.ClearFlag : MailOperation.SetFlag,
            MailOperation.MarkAsRead or MailOperation.MarkAsUnread => items.All(static item => item.IsRead) ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead,
            MailOperation.Archive when ViewModel?.IsArchiveSpecialFolder == true => MailOperation.UnArchive,
            _ => operation
        };
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

    // ---- Multi-select (canvas Mail inbox redesign "selection mode") ----
    // Tapping a sender picture or long-pressing a row selects. The docked search bar turns into the contextual bar
    // (Close, "N selected", Select all) and the bulk actions sit in a floating toolbar in place of the New mail button.

    /// <summary>Starts multi-select, with <paramref name="row"/> selected when it came from a picture tap or a long press.</summary>
    private void StartSelectionMode(MailListRow? row)
    {
        if (_adapter is null || ViewModel is not { } viewModel) return;
        if (!_selecting)
        {
            _selecting = true;
            _adapter.ClearSelection();
            viewModel.IsMultiSelectionModeEnabled = true;
            _adapter.IsSelectionMode = true;
            _searchBar?.BeginContextual(string.Empty, EndSelectionMode);
            BuildContextualMenu();
            Chrome?.SuppressFab(true);
            ShowSelectionToolbar(true);
        }
        if (row is not null) _adapter.SetSelected(row, true);
        UpdateActionMode();
    }

    /// <summary>A picture tap: starts multi-select with the row, or toggles it while selecting.</summary>
    private void AvatarTapped(MailListRow row)
    {
        if (_adapter is null) return;
        if (_selecting) _adapter.SetSelected(row, !_adapter.IsSelected(row));
        else StartSelectionMode(row);
    }

    /// <summary>Leaves multi-select (Close, back, a destructive action) and drops the selection.</summary>
    private void EndSelectionMode()
    {
        if (!_selecting) return;
        _selecting = false;
        _searchBar?.EndContextual();
        if (_toolbar?.Context is { } context) BuildMenu(context);
        ShowSelectionToolbar(false);
        if (!IsSearchPanelOpen) Chrome?.SuppressFab(false);
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

    /// <summary>The contextual bar's title and Select all, and the floating toolbar's actions, for the current selection.</summary>
    private void UpdateActionMode()
    {
        if (!_selecting || ViewModel is not { } viewModel) return;
        int count = viewModel.SelectedItemsCount;
        string description = count > 0 ? string.Format(Translator.MailsSelected, count) : Translator.NoMailSelected;
        _searchBar?.SetContextualTitle(string.Format(System.Globalization.CultureInfo.CurrentCulture, Translator.Android_MailList_SelectedCount, count), description);
        _toolbar?.Menu?.FindItem(MenuSelectAll)?.SetTitle(viewModel.IsAllItemsSelected && count > 0 ? Translator.Buttons_UnselectAll : Translator.MacOSMenu_SelectAll);
        UpdateSelectionToolbar();
    }

    private void BuildContextualMenu()
    {
        if (_toolbar?.Menu is not { } menu) return;
        menu.Clear();
        menu.Add(0, MenuSelectAll, 0, Translator.MacOSMenu_SelectAll)?.SetShowAsAction(ShowAsAction.Always | ShowAsAction.WithText);
    }

    /// <summary>The contextual bar's Select all: selects every row, or clears the selection when all are selected.</summary>
    private void ToggleSelectAll()
    {
        if (_adapter is null || ViewModel is not { } viewModel) return;
        if (viewModel.IsAllItemsSelected && viewModel.SelectedItemsCount > 0) _adapter.ClearSelection();
        else _adapter.SelectAll();
    }

    // ---- Floating toolbar: Archive, Delete, Mark read (the tonal primary action), Move, More ----

    private void ShowSelectionToolbar(bool show)
    {
        if (show && _selectionToolbar is null && View?.Context is { } context && _refresh?.Parent is FrameLayout host)
        {
            _selectionToolbar = new LinearLayout(context) { Orientation = Orientation.Horizontal, Elevation = WinoUnits.DpF(context, 6) };
            _selectionToolbar.SetGravity(GravityFlags.CenterVertical);
            int height = WinoUnits.Dp(context, 64);
            var shape = new Google.Android.Material.Shape.MaterialShapeDrawable(
                Google.Android.Material.Shape.ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(height / 2f).Build());
            shape.FillColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.ZoneFill(context));
            shape.SetStroke(Math.Max(1, WinoUnits.Dp(context, 1)), WinoColors.ZoneStroke(context));
            _selectionToolbar.Background = shape;
            _selectionToolbar.SetPadding(WinoUnits.Dp(context, 8), 0, WinoUnits.Dp(context, 8), 0);
            _selectionToolbar.ContentDescription = Translator.Android_MailList_SelectionActions;
            var layout = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, height, GravityFlags.Bottom | GravityFlags.CenterHorizontal)
            {
                BottomMargin = WinoUnits.Dp(context, 16)
            };
            host.AddView(_selectionToolbar, layout);
        }
        if (_selectionToolbar is not null) _selectionToolbar.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        // The last rows stay reachable above the floating toolbar.
        if (_list is not null && _list.Context is { } listContext)
        {
            _list.SetClipToPadding(false);
            _list.SetPadding(_list.PaddingLeft, _list.PaddingTop, _list.PaddingRight, show ? WinoUnits.Dp(listContext, 96) : 0);
        }
        if (show) UpdateSelectionToolbar();
    }

    private void UpdateSelectionToolbar()
    {
        if (_selectionToolbar is not { } bar || bar.Context is not { } context || ViewModel is not { } viewModel) return;
        bar.RemoveAllViews();
        var selected = viewModel.SelectedItems.ToArray();
        bool any = selected.Length > 0;
        var archive = viewModel.IsArchiveSpecialFolder ? MailOperation.UnArchive : MailOperation.Archive;
        bool allRead = any && selected.All(static mail => mail.IsRead);
        var readOperation = allRead ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead;
        Button(MailOperationDisplay.GetTitle(archive), MailOperationDisplay.GetGlyph(archive), () => RunAndClose(archive, viewModel.SelectedItems.ToArray()));
        Button(Translator.MailOperation_Delete, WinoIconGlyph.Delete, () => RunAndClose(MailOperation.SoftDelete, viewModel.SelectedItems.ToArray()));
        Button(MailOperationDisplay.GetTitle(readOperation), allRead ? WinoIconGlyph.MarkUnread : WinoIconGlyph.MarkRead,
            () => RunOperation(readOperation, viewModel.SelectedItems.ToArray(), null), primary: true);
        Button(Translator.MailOperation_Move, WinoIconGlyph.Move, () => ShowMove(viewModel.SelectedItems.Select(static mail => mail.MailCopy).ToArray()));
        View? more = null;
        more = Button(Translator.More, WinoIconGlyph.More, () => ShowMoreActions(more ?? bar));

        View Button(string title, WinoIconGlyph glyph, Action run, bool primary = false)
        {
            var button = new ImageButton(context) { ContentDescription = title, Enabled = any };
            var color = primary ? WinoColors.OnAccent(context) : WinoColors.SecondaryText(context);
            button.SetImageDrawable(WinoIcons.Drawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), color));
            var fill = new global::Android.Graphics.Drawables.GradientDrawable();
            fill.SetCornerRadius(WinoUnits.DpF(context, 24));
            fill.SetColor(primary ? WinoColors.Accent(context) : global::Android.Graphics.Color.Transparent);
            button.Background = new global::Android.Graphics.Drawables.RippleDrawable(
                global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)), fill, fill);
            button.Alpha = any ? 1f : 0.38f;
            AndroidX.AppCompat.Widget.TooltipCompat.SetTooltipText(button, title);
            button.Click += (_, _) => run();
            var layout = new LinearLayout.LayoutParams(WinoUnits.Dp(context, 56), WinoUnits.Dp(context, 48)) { MarginStart = WinoUnits.Dp(context, 2), MarginEnd = WinoUnits.Dp(context, 2) };
            bar.AddView(button, layout);
            return button;
        }
    }

    /// <summary>More: Flag, the rest of the shared actions (Windows MailContextFlyoutBuilder) and Pin.</summary>
    private void ShowMoreActions(View anchor)
    {
        if (ViewModel is not { } viewModel || anchor.Context is not { } context) return;
        var selected = viewModel.SelectedItems.ToArray();
        if (selected.Length == 0) return;
        var popup = new AndroidX.AppCompat.Widget.PopupMenu(context, anchor);
        var actions = new Dictionary<int, Action>();
        void Add(string title, Action run, bool enabled = true)
        {
            int id = actions.Count + 1;
            actions[id] = run;
            popup.Menu.Add(0, id, id, title)?.SetEnabled(enabled);
        }
        bool allFlagged = selected.All(static item => item.IsFlagged);
        Add(allFlagged ? MailOperationDisplay.GetTitle(MailOperation.ClearFlag) : Translator.MailOperation_Flag,
            () => RunOperation(allFlagged ? MailOperation.ClearFlag : MailOperation.SetFlag, selected, null));
        foreach (var available in viewModel.GetAvailableMailActions(selected))
        {
            if (available.Operation is MailOperation.Seperator or MailOperation.Archive or MailOperation.UnArchive or MailOperation.SoftDelete
                or MailOperation.SetFlag or MailOperation.ClearFlag or MailOperation.Move or MailOperation.MarkAsRead or MailOperation.MarkAsUnread) continue;
            var title = MailOperationDisplay.GetTitle(available.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            var operation = available.Operation;
            Add(title, () => RunOperation(operation, selected, selected.FirstOrDefault()), available.IsEnabled);
        }
        bool allPinned = selected.All(static item => item.MailCopy.IsPinned);
        Add(allPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin,
            () => Observe(viewModel.ChangePinnedStatusAsync(selected, !allPinned)));
        popup.MenuItemClick += (_, args) => { if (args.Item?.ItemId is int id && actions.TryGetValue(id, out var run)) run(); };
        popup.Show();
    }

    private void RunAndClose(MailOperation operation, IReadOnlyList<MailItemViewModel> items)
    {
        RunOperation(operation, items, null);
        EndSelectionMode();
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
