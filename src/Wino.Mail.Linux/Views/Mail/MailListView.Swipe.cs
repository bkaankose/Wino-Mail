using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// Swipe actions (Windows <c>WinoSwipeControl</c>, macOS row actions): a touch or two-finger touchpad swipe slides the
/// row and runs the "swipe right" or "swipe left" operation on release past the threshold. Never in select mode, during
/// a drag or on a busy row.
/// </summary>
internal sealed partial class MailListView
{
    private readonly Dictionary<MailRowCell, IDisposable> _swipeCells = new(ReferenceEqualityComparer.Instance);
    private MailRowCell? _swipingCell;

    partial void SetUpSwipeActions()
    {
        var self = _self;
        Bind.Bind(nameof(MailListPageViewModel.IsMultiSelectionModeEnabled), static vm => vm.IsMultiSelectionModeEnabled, enabled =>
        {
            if (enabled) self.Value?._swipingCell?.Swipe.Cancel();
        });
        Bind.Bind(nameof(MailListPageViewModel.IsDragInProgress), static vm => vm.IsDragInProgress, dragging =>
        {
            if (dragging) self.Value?._swipingCell?.Swipe.Cancel();
        });
        Bindings.Own(new ActionDisposable(() =>
        {
            if (self.Value is not { } view) return;
            foreach (var subscription in view._swipeCells.Values) subscription.Dispose();
            view._swipeCells.Clear();
            view._swipingCell = null;
        }));
    }

    partial void SwipeCellSetup(MailRowCell cell)
    {
        var self = _self;
        cell.Swipe.ResolveAction = side => self.Value?.DescribeSwipe(cell, side);
        Action<WinoSwipeSide> committed = side =>
        {
            if (self.Value is not { IsReleased: false } view || cell.Item is not { } row) return;
            var leading = side == WinoSwipeSide.Leading;
#if DEBUG
            view.DebugSwipeCommitted?.Invoke(view.ViewModel.ResolveSwipeOperation(row, leading), Wino.Mail.ViewModels.Collections.MailRowPresentation.Leaves(row).Count());
#endif
            view.Observe(view.ViewModel.ExecuteSwipeAsync(row, leading));
        };
        Action active = () =>
        {
            if (self.Value is not { } view) return;
            if (cell.Swipe.IsActive)
            {
                if (view._swipingCell is { } other && !ReferenceEquals(other, cell)) other.Swipe.Cancel();
                view._swipingCell = cell;
            }
            else if (ReferenceEquals(view._swipingCell, cell)) view._swipingCell = null;
            view.UpdateHover(cell, false);
        };
        cell.Swipe.Committed += committed;
        cell.Swipe.ActiveChanged += active;
        _swipeCells[cell] = new ActionDisposable(() =>
        {
            cell.Swipe.Committed -= committed;
            cell.Swipe.ActiveChanged -= active;
            cell.Swipe.ResolveAction = null;
        });
    }

    partial void SwipeCellTeardown(MailRowCell cell)
    {
        if (_swipeCells.Remove(cell, out var subscription)) subscription.Dispose();
        if (ReferenceEquals(_swipingCell, cell)) _swipingCell = null;
    }

    partial void SwipeCellBound(MailRowCell cell, MailListRow row)
    {
        if (cell.Content is { IsBusy: true }) cell.Swipe.Cancel();
    }

    private WinoSwipeAction? DescribeSwipe(MailRowCell cell, WinoSwipeSide side)
    {
        if (IsReleased || cell.Item is not { } row || ViewModel.IsMultiSelectionModeEnabled || ViewModel.IsDragInProgress || cell.Content is { IsBusy: true }) return null;
        var operation = ViewModel.ResolveSwipeOperation(row, side == WinoSwipeSide.Leading);
        if (operation == MailOperation.None) return null;
        var title = MailOperationDisplay.GetTitle(operation);
        if (string.IsNullOrEmpty(title)) return null;
        var tone = operation switch
        {
            MailOperation.SoftDelete or MailOperation.HardDelete => WinoSwipeTone.Destructive,
            MailOperation.Archive or MailOperation.UnArchive => WinoSwipeTone.Success,
            MailOperation.SetFlag or MailOperation.ClearFlag => WinoSwipeTone.Warning,
            _ => WinoSwipeTone.Accent,
        };
        return new WinoSwipeAction(MailOperationDisplay.GetGlyph(operation), title, tone);
    }

#if DEBUG
    internal Action<MailOperation, int>? DebugSwipeCommitted { get; set; }

    internal MailRowCell? DebugSwipingCell => _swipingCell;

    internal string DebugSwipeState()
    {
        var lines = new System.Text.StringBuilder();
        Cells.ForEachBound((cell, entry) =>
        {
            var subject = entry.Row.SourceItem is Wino.Mail.ViewModels.Data.MailItemViewModel item ? item.Subject : entry.Row.ThreadKey;
            lines.Append(subject).Append('|').Append(cell.Swipe.IsActive).Append('|').Append(cell.Swipe.Side).Append('|')
                .Append(cell.Swipe.Progress.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(cell.Swipe.IsArmed).Append('|').Append(cell.Swipe.Offset.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(cell.Hover.IsShown).Append('\n');
        });
        return lines.ToString();
    }
#endif
}
