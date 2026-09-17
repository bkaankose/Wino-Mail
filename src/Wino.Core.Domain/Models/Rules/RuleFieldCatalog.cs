using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Rules;

/// <summary>
/// Maps each rule condition/action to the kind of value it takes. Shared by the rule UI (to drive the
/// adaptive value editor and seed defaults) and by the run-now planner. Display labels live in the
/// UI layer (localized), not here.
/// </summary>
public static class RuleFieldCatalog
{
    public static RuleValueKind ValueKind(RuleConditionField field) => field switch
    {
        RuleConditionField.From or RuleConditionField.SentTo => RuleValueKind.Sender,
        RuleConditionField.SubjectContains or RuleConditionField.BodyContains => RuleValueKind.Text,
        RuleConditionField.Importance => RuleValueKind.Importance,
        RuleConditionField.HasAttachment => RuleValueKind.None,
        _ => RuleValueKind.Text
    };

    public static RuleValueKind ValueKind(RuleActionType type) => type switch
    {
        RuleActionType.Move or RuleActionType.Copy => RuleValueKind.Folder,
        RuleActionType.Categorize => RuleValueKind.Category,
        RuleActionType.Forward => RuleValueKind.Sender,
        RuleActionType.MarkRead or RuleActionType.Delete => RuleValueKind.None,
        _ => RuleValueKind.Text
    };
}
