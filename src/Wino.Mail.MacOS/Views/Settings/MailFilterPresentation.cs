using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Shared look of the mail filter pages: the Windows XamlHelpers filter glyphs and tones, and the
/// natural-language rule summary built from the editor's MailFilterEditor_Summary* strings.
/// </summary>
internal static class MailFilterPresentation
{
    /// <summary>Windows XamlHelpers.GetFilterFieldGlyph.</summary>
    public static WinoIconGlyph FieldGlyph(MailFilterConditionField? field) => field switch
    {
        MailFilterConditionField.FromAddress => WinoIconGlyph.Mail,
        MailFilterConditionField.FromName => WinoIconGlyph.Person,
        MailFilterConditionField.Subject => WinoIconGlyph.Message,
        MailFilterConditionField.PreviewText => WinoIconGlyph.TextDescription,
        MailFilterConditionField.HasAttachments => WinoIconGlyph.Attachment,
        MailFilterConditionField.Importance => WinoIconGlyph.Important,
        _ => WinoIconGlyph.Filter,
    };

    /// <summary>Windows XamlHelpers.GetFilterActionGlyph.</summary>
    public static WinoIconGlyph ActionGlyph(MailFilterActionType? action) => action switch
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

    /// <summary>Windows GetFilterActionIconForeground: success, caution, critical or attention (accent).</summary>
    public static NSColor ActionTone(MailFilterActionType? action) => action switch
    {
        MailFilterActionType.MarkRead or MailFilterActionType.MarkAsNotJunk => WinoStyle.Success,
        MailFilterActionType.SetFlag or MailFilterActionType.ClearFlag => WinoStyle.Caution,
        MailFilterActionType.MoveToJunk or MailFilterActionType.SoftDelete or MailFilterActionType.HardDelete => WinoStyle.Critical,
        _ => WinoStyle.Accent
    };

    public static string FieldName(MailFilterConditionField field) => field switch
    {
        MailFilterConditionField.FromAddress => Translator.MailFilterField_FromAddress,
        MailFilterConditionField.FromName => Translator.MailFilterField_FromName,
        MailFilterConditionField.Subject => Translator.MailFilterField_Subject,
        MailFilterConditionField.PreviewText => Translator.MailFilterField_PreviewText,
        MailFilterConditionField.HasAttachments => Translator.MailFilterField_HasAttachments,
        MailFilterConditionField.Importance => Translator.MailFilterField_Importance,
        _ => field.ToString()
    };

    public static string OperatorName(MailFilterConditionOperator value) => value switch
    {
        MailFilterConditionOperator.Equals => Translator.MailFilterOperator_Equals,
        MailFilterConditionOperator.NotEquals => Translator.MailFilterOperator_NotEquals,
        MailFilterConditionOperator.Contains => Translator.MailFilterOperator_Contains,
        MailFilterConditionOperator.NotContains => Translator.MailFilterOperator_NotContains,
        MailFilterConditionOperator.StartsWith => Translator.MailFilterOperator_StartsWith,
        MailFilterConditionOperator.EndsWith => Translator.MailFilterOperator_EndsWith,
        _ => value.ToString()
    };

    public static string ActionName(MailFilterActionType action) => action switch
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

    private static string ValueText(MailFilterCondition condition)
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

    /// <summary>
    /// The editor's live summary ("When Subject contains “x”, then Move to folder (GitHub).") for a stored
    /// filter. Read-only provider rules without parsed conditions keep their provider summary.
    /// </summary>
    public static string Summary(MailFilter filter, IReadOnlyDictionary<string, string> folderNames, string fallback)
    {
        if (filter.Conditions.Count == 0 && filter.Actions.Count == 0)
            return string.IsNullOrWhiteSpace(filter.ProviderSummary) ? fallback : filter.ProviderSummary;

        var joiner = filter.MatchMode == MailFilterMatchMode.Any
            ? Translator.MailFilterEditor_SummaryJoinerAny
            : Translator.MailFilterEditor_SummaryJoinerAll;
        var conditions = string.Join(joiner, filter.Conditions.OrderBy(condition => condition.Order).Select(condition =>
            string.Format(Translator.MailFilterEditor_SummaryCondition, FieldName(condition.Field), OperatorName(condition.Operator), ValueText(condition))));
        var actions = string.Join(Translator.MailFilterEditor_SummaryActionJoiner, filter.Actions.OrderBy(action => action.Order).Select(action =>
            action.Type == MailFilterActionType.Move
                && !string.IsNullOrWhiteSpace(action.TargetRemoteFolderId)
                && folderNames.TryGetValue(action.TargetRemoteFolderId, out var folder)
                ? string.Format(Translator.MailFilterEditor_SummaryActionWithFolder, ActionName(action.Type), folder)
                : ActionName(action.Type)));

        return string.IsNullOrEmpty(conditions) || string.IsNullOrEmpty(actions)
            ? fallback
            : string.Format(Translator.MailFilterEditor_SummaryFormat, conditions, actions);
    }

    /// <summary>A 32pt rounded tile with a tinted wash and a glyph in the tone colour (Windows filter row tile).</summary>
    public static (WinoSurfaceView Tile, WinoIconView Icon) Tile(WinoIconGlyph glyph, NSColor tone, double size = 32, double glyphSize = 15)
    {
        var tile = new WinoSurfaceView { Fill = tone.ColorWithAlphaComponent(0.12f), CornerRadius = 6, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(tile, size, size);
        var icon = new WinoIconView(glyph, glyphSize, tone) { Colorful = false };
        tile.AddSubview(icon);
        NSLayoutConstraint.ActivateConstraints(
        [
            icon.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor),
            icon.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor),
            icon.WidthAnchor.ConstraintEqualTo((nfloat)glyphSize),
            icon.HeightAnchor.ConstraintEqualTo((nfloat)glyphSize)
        ]);
        tile.AccessibilityElement = false;
        return (tile, icon);
    }
}
