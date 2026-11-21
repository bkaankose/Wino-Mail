#if DEBUG
using System.Globalization;
using System.Text;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.Mail;

internal sealed partial class MailListView
{
    internal bool DebugIsLoadingMore => ViewModel.LoadMoreItemsCommand.IsRunning;

    internal bool DebugCanLoadMore => ViewModel.LoadMoreItemsCommand.CanExecute(null);

    internal string DebugFolderName => ViewModel.ActiveFolder?.FolderName ?? string.Empty;

    internal string DebugState()
    {
        var text = new StringBuilder();
        void Line(string line) => text.Append(line).Append('\n');
        var top = Cells.TryGetTop(_scroll, out var item, out _) ? item : null;
        var groups = 0;
        var previous = -1;
        foreach (var entry in _shown)
        {
            if (entry.GroupOrdinal == previous) continue;
            previous = entry.GroupOrdinal;
            groups++;
        }
        var orderOk = _sections.Model.GetNItems() == _shown.Count;
        if (orderOk && _shown.Count > 0)
        {
            var samples = Math.Min(200, _shown.Count);
            for (var index = 0; index < samples && orderOk; index++)
            {
                var position = (uint)((long)index * (_shown.Count - 1) / Math.Max(1, samples - 1));
                orderOk = ReferenceEquals(_sections.ItemAt(position), _shown[(int)position]);
            }
        }
        var adjustment = _scroll.GetVadjustment();
        Line(string.Create(CultureInfo.InvariantCulture, $"rows={_shown.Count}"));
        Line(string.Create(CultureInfo.InvariantCulture, $"groups={(_grouped ? groups : 0)}"));
        Line($"grouped={_grouped}");
        Line($"page={_page}");
        Line($"sticky={(_stickyShown ? _sticky.Title : string.Empty)}");
        Line($"sticky_visible={_stickyShown}");
        Line($"top={top?.Key ?? string.Empty}");
        Line($"top_group={top?.GroupTitle ?? string.Empty}");
        Line(string.Create(CultureInfo.InvariantCulture, $"selected={SelectedRowCountNow()}"));
        Line(string.Create(CultureInfo.InvariantCulture, $"selected_keys={string.Join(',', ListViews.SelectedPositions(Selection).Take(10).Select(p => p < _shown.Count ? _shown[(int)p].Key : "?"))}"));
        Line(string.Create(CultureInfo.InvariantCulture, $"selected_positions={string.Join(',', ListViews.SelectedPositions(Selection).Take(10))}"));
        Line(string.Create(CultureInfo.InvariantCulture, $"cells={Cells.CellCount}"));
        Line(string.Create(CultureInfo.InvariantCulture, $"bound={Cells.BoundCount}"));
        Line($"loading_more={ViewModel.LoadMoreItemsCommand.IsRunning}");
        Line(string.Create(CultureInfo.InvariantCulture, $"scroll={adjustment.GetValue():F0}/{adjustment.GetUpper():F0} page={adjustment.GetPageSize():F0}"));
        Line(string.Create(CultureInfo.InvariantCulture, $"entries_known={_byKey.Count}"));
        Line($"order_ok={orderOk.ToString().ToLowerInvariant()}");
        Line($"folder={DebugFolderName}");
        for (var index = 0; index < Math.Min(5, _shown.Count); index++) Line($"first {_shown[index].Key} | {_shown[index].GroupTitle}");
        for (var index = Math.Max(5, _shown.Count - 2); index < _shown.Count; index++) Line($"last {_shown[index].Key} | {_shown[index].GroupTitle}");
        return text.ToString();
    }

    /// <summary>A fraction of the scroll range (<c>0.5</c>) or a row key.</summary>
    internal void DebugScrollTo(string target)
    {
        if (double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
        {
            var adjustment = _scroll.GetVadjustment();
            adjustment.SetValue(adjustment.GetLower() + fraction * Math.Max(0, adjustment.GetUpper() - adjustment.GetPageSize() - adjustment.GetLower()));
            return;
        }
        if (_byKey.TryGetValue(target, out var entry)) ScrollToEntry(entry);
    }
}
#endif
