namespace Wino.Mail.Controls.Core;

/// <summary>A selection snapshot and the signature that tells whether it differs from the last one published.</summary>
public sealed record MailListSelectionResult(MailListSelectionSnapshot Snapshot, string Signature);

/// <summary>
/// Turns the rows a native list shows as selected into the <see cref="MailListSelectionSnapshot"/> the mail list
/// ViewModel applies (the Windows WinoMailListView rule, shared by the Mac, iPhone, iPad and Android lists): a selected
/// thread head selects all of its leaves and the whole thread, a thread whose leaves are all selected counts as fully
/// selected, and the active item is the explicitly current row or else the last selected one. The signature lets a list
/// skip publishing the same selection twice.
/// </summary>
public static class MailListSelectionSnapshotBuilder
{
    /// <param name="selectedRows">The selected rows in display order.</param>
    /// <param name="projection">The projection the rows belong to (full-thread detection).</param>
    /// <param name="activeRow">The list's current row when it has one; otherwise the last selected row is active.</param>
    public static MailListSelectionResult Build(IReadOnlyList<MailListRow> selectedRows, MailListProjection projection, MailListRow? activeRow = null)
    {
        ArgumentNullException.ThrowIfNull(selectedRows);
        ArgumentNullException.ThrowIfNull(projection);

        var items = new List<IMailListSourceItem>();
        var ids = new HashSet<Guid>();
        var fullThreads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in selectedRows)
        {
            if (row.IsThreadHead && row.Thread is { } thread)
            {
                fullThreads.Add(thread.Key);
                foreach (var leaf in thread.Items)
                    if (ids.Add(leaf.StableId)) items.Add(leaf);
            }
            else if (ids.Add(row.SourceItem.StableId)) items.Add(row.SourceItem);
        }
        foreach (var thread in projection.Threads)
            if (thread.Items.Count > 0 && thread.Items.All(leaf => ids.Contains(leaf.StableId))) fullThreads.Add(thread.Key);

        var active = activeRow?.SourceItem ?? (selectedRows.Count > 0 ? selectedRows[^1].SourceItem : null);
        var signature = string.Join(",", ids.OrderBy(static id => id)) + "|" + active?.StableId;
        return new MailListSelectionResult(new MailListSelectionSnapshot(items, fullThreads, active), signature);
    }

    /// <summary>The identity of a row in a native list: the thread key for thread heads, the mail id otherwise.</summary>
    public static string RowKey(MailListRow row)
        => row.IsThreadHead ? "T:" + row.ThreadKey : "I:" + row.SourceItem.StableId.ToString("N");
}
