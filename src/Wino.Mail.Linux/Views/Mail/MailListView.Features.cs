using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Linux.MailList;

namespace Wino.Mail.Linux.Views.Mail;

internal sealed partial class MailListView
{
    // Implemented in MailListView.<Feature>.cs by the package that owns the feature; an unimplemented hook compiles to nothing.
    partial void SetUpContextMenu();
    partial void SetUpHoverActions();
    partial void SetUpSwipeActions();
    partial void SetUpDrag();
    partial void SetUpSearch();
    partial void SetUpSearchFilters();
    partial void SetUpDraftSelection();

    partial void ContextMenuCellSetup(MailRowCell cell);
    partial void HoverCellSetup(MailRowCell cell);
    partial void SwipeCellSetup(MailRowCell cell);
    partial void DragCellSetup(MailRowCell cell);

    partial void HoverCellBound(MailRowCell cell, MailListRow row);
    partial void SwipeCellBound(MailRowCell cell, MailListRow row);
    partial void DragCellBound(MailRowCell cell, MailListRow row);

    partial void HoverCellRefreshed(MailRowCell cell, WinoMailRowModel model);

    partial void ContextMenuCellTeardown(MailRowCell cell);
    partial void HoverCellTeardown(MailRowCell cell);
    partial void SwipeCellTeardown(MailRowCell cell);
    partial void DragCellTeardown(MailRowCell cell);

    private void SetUpFeatures() { SetUpContextMenu(); SetUpHoverActions(); SetUpSwipeActions(); SetUpDrag(); SetUpSearch(); SetUpSearchFilters(); SetUpDraftSelection(); }
    private void FeatureCellSetup(MailRowCell cell) { ContextMenuCellSetup(cell); HoverCellSetup(cell); SwipeCellSetup(cell); DragCellSetup(cell); }
    private void FeatureCellBound(MailRowCell cell, MailListRow row) { HoverCellBound(cell, row); SwipeCellBound(cell, row); DragCellBound(cell, row); }
    private void FeatureCellRefreshed(MailRowCell cell, WinoMailRowModel model) { HoverCellRefreshed(cell, model); }
    private void FeatureCellTeardown(MailRowCell cell) { ContextMenuCellTeardown(cell); HoverCellTeardown(cell); SwipeCellTeardown(cell); DragCellTeardown(cell); }

#if DEBUG
    /// <summary><c>#n</c> is the n-th mail row in screen order (0-based); any other text is the first row whose subject equals it.</summary>
    internal MailListRow? DebugFindRow(string selector)
    {
        if (selector.StartsWith('#') && int.TryParse(selector.AsSpan(1), out var index)) return index >= 0 && index < _shown.Count ? _shown[index].Row : null;
        foreach (var entry in _shown)
        {
            if (entry.Row.SourceItem is Wino.Mail.ViewModels.Data.MailItemViewModel item && string.Equals(item.Subject, selector, StringComparison.Ordinal)) return entry.Row;
        }
        return null;
    }

    /// <summary>The cell showing <paramref name="row"/>; scrolls it into view first when it has none, so the caller may need to look again a turn later.</summary>
    internal MailRowCell? DebugCellOf(MailListRow row)
    {
        if (!_byRow.TryGetValue(row, out var entry)) return null;
        if (Cells.CellOf(entry) is { } cell) return cell;
        if (PositionOf(entry) is { } position) Wino.Presentation.Linux.Lists.ListViews.ScrollTo(List, position);
        return null;
    }
#endif
}
