namespace Wino.Core.Domain.Enums;

/// <summary>
/// What the reader's privacy button shows for the open message.
/// </summary>
public enum PrivacyButtonState
{
    /// <summary>The policy applied and nothing was blocked.</summary>
    Plain,

    /// <summary>Block trackers removed at least one tracker. The button shows the count.</summary>
    Count,

    /// <summary>Remote content was withheld by Block all, a Block rule, Junk or a per-message block.</summary>
    Blocked,

    /// <summary>A sender rule, the contacts setting or the recipients setting loaded everything.</summary>
    Rule
}
