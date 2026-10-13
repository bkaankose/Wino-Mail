#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Presentation;

/// <summary>Semantic colour of a mail filter action tile; each platform maps it to its system fill pair.</summary>
public enum MailFilterActionTone
{
    /// <summary>The accent (attention) colour.</summary>
    Attention,
    Success,
    Caution,
    Critical
}

/// <summary>
/// Shared look of the mail filter pages: field and action glyphs, the action tone, display names
/// and the natural-language rule summary built from the editor's MailFilterEditor_Summary* strings.
/// </summary>
public static class MailFilterDisplay
{
    public static WinoIconGlyph GetFieldGlyph(MailFilterConditionField? field) => field switch
    {
        MailFilterConditionField.FromAddress => WinoIconGlyph.Mail,
        MailFilterConditionField.FromName => WinoIconGlyph.Person,
        MailFilterConditionField.Subject => WinoIconGlyph.Message,
        MailFilterConditionField.PreviewText => WinoIconGlyph.TextDescription,
        MailFilterConditionField.HasAttachments => WinoIconGlyph.Attachment,
        MailFilterConditionField.Importance => WinoIconGlyph.Important,
        _ => WinoIconGlyph.Filter,
    };

    public static WinoIconGlyph GetActionGlyph(MailFilterActionType? action) => action switch
    {
        MailFilterActionType.Move => WinoIconGlyph.Move,
        MailFilterActionType.Archive => WinoIconGlyph.Archive,
        MailFilterActionType.SoftDelete or MailFilterActionType.HardDelete => WinoIconGlyph.Delete,
        MailFilterActionType.MarkRead => WinoIconGlyph.MarkRead,
        MailFilterActionType.MarkUnread => WinoIconGlyph.MarkUnread,
        MailFilterActionType.SetFlag => WinoIconGlyph.Flag,
        MailFilterActionType.ClearFlag => WinoIconGlyph.ClearFlag,
        MailFilterActionType.MoveToJunk => WinoIconGlyph.Blocked,
        MailFilterActionType.MarkAsNotJunk => WinoIconGlyph.Checkmark,
        _ => WinoIconGlyph.Sparkle,
    };

    public static MailFilterActionTone GetActionTone(MailFilterActionType? action) => action switch
    {
        MailFilterActionType.MarkRead or MailFilterActionType.MarkAsNotJunk => MailFilterActionTone.Success,
        MailFilterActionType.SetFlag or MailFilterActionType.ClearFlag => MailFilterActionTone.Caution,
        MailFilterActionType.MoveToJunk or MailFilterActionType.SoftDelete or MailFilterActionType.HardDelete => MailFilterActionTone.Critical,
        _ => MailFilterActionTone.Attention
    };

    /// <summary>On Wino filters these actions move or remove the message, so they cannot be combined with other actions.</summary>
    public static bool IsExclusiveAction(MailFilterActionType? action)
        => action is MailFilterActionType.Move
            or MailFilterActionType.Archive
            or MailFilterActionType.MoveToJunk
            or MailFilterActionType.MarkAsNotJunk
            or MailFilterActionType.SoftDelete
            or MailFilterActionType.HardDelete;

    public static string GetFieldName(MailFilterConditionField field) => field switch
    {
        MailFilterConditionField.FromAddress => Translator.MailFilterField_FromAddress,
        MailFilterConditionField.FromName => Translator.MailFilterField_FromName,
        MailFilterConditionField.Subject => Translator.MailFilterField_Subject,
        MailFilterConditionField.PreviewText => Translator.MailFilterField_PreviewText,
        MailFilterConditionField.HasAttachments => Translator.MailFilterField_HasAttachments,
        MailFilterConditionField.Importance => Translator.MailFilterField_Importance,
        _ => field.ToString()
    };

    public static string GetOperatorName(MailFilterConditionOperator value) => value switch
    {
        MailFilterConditionOperator.Equals => Translator.MailFilterOperator_Equals,
        MailFilterConditionOperator.NotEquals => Translator.MailFilterOperator_NotEquals,
        MailFilterConditionOperator.Contains => Translator.MailFilterOperator_Contains,
        MailFilterConditionOperator.NotContains => Translator.MailFilterOperator_NotContains,
        MailFilterConditionOperator.StartsWith => Translator.MailFilterOperator_StartsWith,
        MailFilterConditionOperator.EndsWith => Translator.MailFilterOperator_EndsWith,
        _ => value.ToString()
    };

    public static string GetActionName(MailFilterActionType action) => action switch
    {
        MailFilterActionType.MarkRead => Translator.MailFilterAction_MarkRead,
        MailFilterActionType.MarkUnread => Translator.MailFilterAction_MarkUnread,
        MailFilterActionType.SetFlag => Translator.MailFilterAction_SetFlag,
        MailFilterActionType.ClearFlag => Translator.MailFilterAction_ClearFlag,
        MailFilterActionType.Move => Translator.MailFilterAction_Move,
        MailFilterActionType.Archive => Translator.MailFilterAction_Archive,
        MailFilterActionType.MoveToJunk => Translator.MailFilterAction_MoveToJunk,
        MailFilterActionType.MarkAsNotJunk => Translator.MailFilterAction_MarkAsNotJunk,
        MailFilterActionType.SoftDelete => Translator.MailFilterAction_SoftDelete,
        MailFilterActionType.HardDelete => Translator.MailFilterAction_HardDelete,
        _ => action.ToString()
    };

    /// <summary>
    /// The editor's live summary ("When Subject contains “x”, then Move to folder (GitHub).") for a stored
    /// filter. Read-only provider rules without parsed conditions keep their provider summary;
    /// <paramref name="fallback"/> is used when nothing better can be said.
    /// </summary>
    public static string GetSummary(MailFilter filter, IReadOnlyDictionary<string, string> folderNames, string fallback)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(folderNames);

        var conditionList = filter.Conditions ?? [];
        var actionList = filter.Actions ?? [];

        if (conditionList.Count == 0 && actionList.Count == 0)
            return string.IsNullOrWhiteSpace(filter.ProviderSummary) ? fallback : filter.ProviderSummary;

        var joiner = filter.MatchMode == MailFilterMatchMode.Any
            ? Translator.MailFilterEditor_SummaryJoinerAny
            : Translator.MailFilterEditor_SummaryJoinerAll;

        var conditions = string.Join(joiner, conditionList.OrderBy(condition => condition.Order).Select(condition =>
            string.Format(Translator.MailFilterEditor_SummaryCondition, GetFieldName(condition.Field), GetOperatorName(condition.Operator), GetValueText(condition))));

        var actions = string.Join(Translator.MailFilterEditor_SummaryActionJoiner, actionList.OrderBy(action => action.Order).Select(action =>
            action.Type == MailFilterActionType.Move
                && !string.IsNullOrWhiteSpace(action.TargetRemoteFolderId)
                && folderNames.TryGetValue(action.TargetRemoteFolderId, out var folder)
                ? string.Format(Translator.MailFilterEditor_SummaryActionWithFolder, GetActionName(action.Type), folder)
                : GetActionName(action.Type)));

        return string.IsNullOrEmpty(conditions) || string.IsNullOrEmpty(actions)
            ? fallback
            : string.Format(Translator.MailFilterEditor_SummaryFormat, conditions, actions);
    }

    private static string GetValueText(MailFilterCondition condition)
    {
        var value = condition.Value?.Trim();
        if (string.IsNullOrEmpty(value)) return "…";

        return condition.Field switch
        {
            MailFilterConditionField.HasAttachments when bool.TryParse(value, out var flag) => flag ? Translator.Buttons_Yes : Translator.Buttons_No,
            MailFilterConditionField.Importance when Enum.TryParse<MailImportance>(value, true, out var importance) => importance switch
            {
                MailImportance.Low => Translator.MailFilterImportance_Low,
                MailImportance.High => Translator.MailFilterImportance_High,
                _ => Translator.MailFilterImportance_Normal
            },
            _ => value
        };
    }
}
