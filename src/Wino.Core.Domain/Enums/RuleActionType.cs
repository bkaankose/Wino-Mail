namespace Wino.Core.Domain.Enums;

/// <summary>
/// The inbox-rule actions Wino models. A subset of the EWS RuleActions: "Flag for follow-up" and
/// "Play a sound" from the Outlook UI have no server-side equivalent and are intentionally omitted.
/// </summary>
public enum RuleActionType
{
    Move,
    Copy,
    Categorize,
    MarkRead,
    Forward,
    Delete
}
