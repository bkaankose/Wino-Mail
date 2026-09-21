namespace Wino.Core.Domain.Enums;

/// <summary>
/// The inbox-rule conditions Wino models. A deliberate subset of the EWS RulePredicates surface.
/// Rules carrying predicates outside this set are treated as read-only.
/// </summary>
public enum RuleConditionField
{
    From,
    SubjectContains,
    BodyContains,
    SentTo,
    Importance,
    HasAttachment
}
