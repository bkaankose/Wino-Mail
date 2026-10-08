namespace Wino.Mail.Controls.Core.ContextFlyout;

public readonly record struct ContextFlyoutFilterEntry(
    bool IsSeparator,
    string Text = "",
    string Breadcrumb = "",
    string SearchKeywords = "");

public static class ContextFlyoutFilter
{
    public static IReadOnlyList<int> GetVisibleIndexes(
        IReadOnlyList<ContextFlyoutFilterEntry> entries,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var terms = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var visibleIndexes = new List<int>();
        var pendingSeparatorIndex = -1;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.IsSeparator)
            {
                if (visibleIndexes.Count > 0)
                {
                    pendingSeparatorIndex = index;
                }

                continue;
            }

            var searchableText = $"{entry.Text} {entry.Breadcrumb} {entry.SearchKeywords}";
            if (!terms.All(term => searchableText.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }

            if (pendingSeparatorIndex >= 0)
            {
                visibleIndexes.Add(pendingSeparatorIndex);
                pendingSeparatorIndex = -1;
            }

            visibleIndexes.Add(index);
        }

        return visibleIndexes;
    }
}
