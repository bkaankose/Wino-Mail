using WinRT;

namespace Wino.Mail.Controls.SearchBar;

/// <summary>
/// One row of the recent-searches list that <see cref="WinoSearchBar"/> shows in its suggestion list
/// while the field is empty. The last row of a non-empty list is the clear-history action.
/// </summary>
[GeneratedBindableCustomProperty]
public sealed partial class SearchBarHistoryEntry
{
    internal SearchBarHistoryEntry(string text, string glyph, bool isClearAction)
    {
        Text = text;
        Glyph = glyph;
        IsClearAction = isClearAction;
    }

    public string Text { get; }

    public string Glyph { get; }

    public bool IsClearAction { get; }

    /// <summary>What the field shows while the row is highlighted. The clear action leaves it empty.</summary>
    public string QueryText => IsClearAction ? string.Empty : Text;

    internal static SearchBarHistoryEntry Query(string text) => new(text, "", false);

    internal static SearchBarHistoryEntry ClearAction(string text) => new(text, "", true);

    public override string ToString() => Text;
}
