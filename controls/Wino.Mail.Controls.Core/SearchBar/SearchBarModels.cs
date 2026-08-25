#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.Controls.Core.SearchBar;

public enum SearchBarMode
{
    Mail,
    Contacts,
    Calendar,
    Tasks,
    Settings,
}

public enum SearchBarScope
{
    CurrentFolder,
    CurrentAccount,
    AllAccounts,
}

public enum SearchBarReach
{
    DownloadedOnly,
    IncludeServer,
}

public enum SearchBarDateRange
{
    AnyTime,
    Today,
    LastSevenDays,
    LastThirtyDays,
}

public enum SearchBarSubmissionOrigin
{
    KeyboardOrQueryIcon,
    Suggestion,
    History,
    SearchPanel,
}

public sealed record SearchBarFilterSnapshot(
    SearchBarScope Scope,
    SearchBarReach Reach,
    string Sender,
    SearchBarDateRange DateRange,
    bool HasAttachments,
    bool IsUnread,
    bool IsFlagged);

public sealed class SearchBarSubmittedEventArgs(
    string queryText,
    object? chosenSuggestion,
    SearchBarSubmissionOrigin origin,
    SearchBarMode mode,
    bool isSemanticSearchEnabled,
    SearchBarFilterSnapshot filters) : EventArgs
{
    public string QueryText { get; } = queryText;

    public object? ChosenSuggestion { get; } = chosenSuggestion;

    public SearchBarSubmissionOrigin Origin { get; } = origin;

    public SearchBarMode Mode { get; } = mode;

    public bool IsSemanticSearchEnabled { get; } = isSemanticSearchEnabled;

    public SearchBarFilterSnapshot Filters { get; } = filters;
}

public sealed class SearchBarSuggestion
{
    public SearchBarSuggestion()
    {
    }

    public SearchBarSuggestion(string title, string subtitle, object? tag = null)
    {
        Title = title;
        Subtitle = subtitle;
        Tag = tag;
    }

    public string Title { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    public object? Tag { get; set; }
}

#if WINRT_EXPOSED
[GeneratedBindableCustomProperty]
#endif
public sealed partial class SearchBarContactSuggestion
{
    public string DisplayName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Initials { get; set; } = string.Empty;

    public object? ContactPicture { get; set; }

    public object? Tag { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Address : DisplayName;
}

#if WINRT_EXPOSED
[GeneratedBindableCustomProperty]
#endif
public sealed partial class SearchBarOptionItem
{
    public SearchBarOptionItem()
    {
    }

    public SearchBarOptionItem(int value, string title)
    {
        Value = value;
        Title = title;
    }

    public int Value { get; set; }

    public string Title { get; set; } = string.Empty;

    public override string ToString() => Title;
}

public sealed class SearchBarTextChangedEventArgs(string text, bool isUserInput) : EventArgs
{
    public string Text { get; } = text;

    public bool IsUserInput { get; } = isUserInput;
}

public sealed class SearchBarSenderQueryEventArgs(string queryText) : EventArgs
{
    public string QueryText { get; } = queryText;
}
