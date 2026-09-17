using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Rules;

namespace Wino.Dialogs.Rules;

/// <summary>An option in the condition-field / action-type dropdowns (enum value + localized label).</summary>
public sealed class RuleFieldOption
{
    public RuleConditionField Field { get; init; }
    public string Label { get; init; } = string.Empty;
}

public sealed class RuleActionOption
{
    public RuleActionType Type { get; init; }
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// Shared base for an editable condition/action row. Carries the adaptive value-editor surface so the
/// same value-editor markup works for both: the visible child control is chosen by <see cref="ValueKind"/>.
/// </summary>
public abstract partial class RuleRowBase : ObservableObject
{
    private RuleValueKind _valueKind;
    public RuleValueKind ValueKind
    {
        get => _valueKind;
        set { if (SetProperty(ref _valueKind, value)) RaiseValueEditorChanged(); }
    }

    // Each editor control binds to its OWN backing value. The controls are overlaid in one cell, so a
    // single shared TwoWay Value made them fight (combo coerces unmatched to null, textbox coerces
    // null to "") and recurse to a stack-overflow crash. Separate props can't fight.
    private string? _folderValue;
    public string? FolderValue
    {
        get => _folderValue;
        set
        {
            if (SetProperty(ref _folderValue, value))
                OnPropertyChanged(nameof(SelectedFolder));
        }
    }

    /// <summary>The folder picker's selected item; the rule stores the folder's remote id in <see cref="FolderValue"/>.</summary>
    public MailItemFolder? SelectedFolder
    {
        get => Folders.FirstOrDefault(f => f.RemoteFolderId == _folderValue);
        set { if (value != null) FolderValue = value.RemoteFolderId; }
    }

    private string? _categoryValue;
    public string? CategoryValue
    {
        get => _categoryValue;
        set
        {
            if (SetProperty(ref _categoryValue, value))
                OnPropertyChanged(nameof(SelectedCategory));
        }
    }

    /// <summary>The category picker's selected item; the rule stores the category name in <see cref="CategoryValue"/>.</summary>
    public MailCategory? SelectedCategory
    {
        get => Categories.FirstOrDefault(c => c.Name == _categoryValue);
        set { if (value != null) CategoryValue = value.Name; }
    }

    private string? _importanceValue;
    public string? ImportanceValue { get => _importanceValue; set => SetProperty(ref _importanceValue, value); }

    private string? _textValue;
    public string? TextValue { get => _textValue; set => SetProperty(ref _textValue, value); }

    /// <summary>The effective value for the current kind, read when saving the rule.</summary>
    public string? Value => ValueKind switch
    {
        RuleValueKind.Folder => FolderValue,
        RuleValueKind.Category => CategoryValue,
        RuleValueKind.Importance => ImportanceValue,
        RuleValueKind.Sender or RuleValueKind.Text => TextValue,
        _ => null
    };

    /// <summary>Folders for the Move/Copy picker (shared list owned by the dialog).</summary>
    public IReadOnlyList<MailItemFolder> Folders { get; set; } = Array.Empty<MailItemFolder>();

    /// <summary>Categories for the Assign-category picker (shared list owned by the dialog).</summary>
    public IReadOnlyList<MailCategory> Categories { get; set; } = Array.Empty<MailCategory>();

    public IReadOnlyList<string> ImportanceOptions => RuleUiCatalog.ImportanceOptions;

    public Visibility FolderVisibility => Vis(ValueKind == RuleValueKind.Folder);
    public Visibility CategoryVisibility => Vis(ValueKind == RuleValueKind.Category);
    public Visibility ImportanceVisibility => Vis(ValueKind == RuleValueKind.Importance);
    public Visibility TextVisibility => Vis(ValueKind is RuleValueKind.Text or RuleValueKind.Sender);
    public Visibility NoneVisibility => Vis(ValueKind == RuleValueKind.None);

    public string ValuePlaceholder => ValueKind == RuleValueKind.Sender
        ? Translator.Rules_NameOrEmail
        : Translator.Rules_TextToMatch;

    // Sets the kind and seeds the matching backing value (a default for a new row, or the loaded value).
    protected void ApplyValueKind(RuleValueKind kind, bool seedDefault, string? initialValue = null)
    {
        ValueKind = kind;
        var v = initialValue ?? (seedDefault ? RuleUiCatalog.DefaultFor(kind, Folders, Categories) : null);
        switch (kind)
        {
            case RuleValueKind.Folder: FolderValue = v; break;
            case RuleValueKind.Category: CategoryValue = v; break;
            case RuleValueKind.Importance: ImportanceValue = string.IsNullOrEmpty(v) ? Translator.Rules_Importance_High : v; break;
            case RuleValueKind.Sender:
            case RuleValueKind.Text: TextValue = v; break;
        }
    }

    private void RaiseValueEditorChanged()
    {
        OnPropertyChanged(nameof(FolderVisibility));
        OnPropertyChanged(nameof(CategoryVisibility));
        OnPropertyChanged(nameof(ImportanceVisibility));
        OnPropertyChanged(nameof(TextVisibility));
        OnPropertyChanged(nameof(NoneVisibility));
        OnPropertyChanged(nameof(ValuePlaceholder));
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class RuleConditionRow : RuleRowBase
{
    public IReadOnlyList<RuleFieldOption> FieldOptions => RuleUiCatalog.ConditionFields;

    private RuleConditionField _field;
    public RuleConditionField Field
    {
        get => _field;
        set
        {
            if (SetProperty(ref _field, value))
            {
                OnPropertyChanged(nameof(SelectedFieldOption));
                ApplyValueKind(RuleFieldCatalog.ValueKind(value), seedDefault: true);
            }
        }
    }

    // Bound to the ComboBox via SelectedItem (reference match), more reliable than SelectedValue/Path.
    public RuleFieldOption? SelectedFieldOption
    {
        get => FieldOptions.FirstOrDefault(o => o.Field == _field);
        set { if (value != null) Field = value.Field; }
    }

    public RuleConditionRow(IReadOnlyList<MailItemFolder> folders, IReadOnlyList<MailCategory> categories,
        RuleConditionField field = RuleConditionField.From, string? value = null)
    {
        Folders = folders;
        Categories = categories;
        _field = field;
        // Compute ValueKind directly (the setter would no-op when field equals the enum default).
        ApplyValueKind(RuleFieldCatalog.ValueKind(field), seedDefault: value == null, initialValue: value);
    }
}

public sealed partial class RuleActionRow : RuleRowBase
{
    public IReadOnlyList<RuleActionOption> ActionOptions => RuleUiCatalog.ActionTypes;

    private RuleActionType _type;
    public RuleActionType Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(SelectedActionOption));
                ApplyValueKind(RuleFieldCatalog.ValueKind(value), seedDefault: true);
            }
        }
    }

    public RuleActionOption? SelectedActionOption
    {
        get => ActionOptions.FirstOrDefault(o => o.Type == _type);
        set { if (value != null) Type = value.Type; }
    }

    public RuleActionRow(IReadOnlyList<MailItemFolder> folders, IReadOnlyList<MailCategory> categories,
        RuleActionType type = RuleActionType.Move, string? value = null)
    {
        Folders = folders;
        Categories = categories;
        _type = type;
        ApplyValueKind(RuleFieldCatalog.ValueKind(type), seedDefault: value == null, initialValue: value);
    }
}

/// <summary>A row in the Rules &amp; Alerts manager list.</summary>
public sealed partial class RuleListItem : ObservableObject
{
    public RemoteInboxRule Rule { get; }

    public RuleListItem(RemoteInboxRule rule, string summary)
    {
        Rule = rule;
        _summary = summary;
        _isEnabled = rule.IsEnabled;
    }

    public string Name => Rule.Name;
    public bool IsReadOnly => Rule.IsReadOnly;
    public bool StopProcessing => Rule.StopProcessing;

    /// <summary>Read-only rules can't be enabled/disabled here (a Set would drop unmodeled parts).</summary>
    public bool CanToggle => !Rule.IsReadOnly;

    public Visibility StopBadgeVisibility => Rule.StopProcessing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadOnlyBadgeVisibility => Rule.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
                Rule.IsEnabled = value;
        }
    }

    private string _summary;
    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }
}

/// <summary>Localized dropdown options, value defaults, and the plain-English rule summary.</summary>
public static class RuleUiCatalog
{
    public static IReadOnlyList<string> ImportanceOptions { get; } = new[]
    {
        Translator.Rules_Importance_High,
        Translator.Rules_Importance_Normal,
        Translator.Rules_Importance_Low
    };

    public static IReadOnlyList<RuleFieldOption> ConditionFields { get; } = new[]
    {
        new RuleFieldOption { Field = RuleConditionField.From, Label = Translator.Rules_Field_From },
        new RuleFieldOption { Field = RuleConditionField.SubjectContains, Label = Translator.Rules_Field_SubjectContains },
        new RuleFieldOption { Field = RuleConditionField.BodyContains, Label = Translator.Rules_Field_BodyContains },
        new RuleFieldOption { Field = RuleConditionField.SentTo, Label = Translator.Rules_Field_SentTo },
        new RuleFieldOption { Field = RuleConditionField.Importance, Label = Translator.Rules_Field_Importance },
        new RuleFieldOption { Field = RuleConditionField.HasAttachment, Label = Translator.Rules_Field_HasAttachment }
    };

    public static IReadOnlyList<RuleActionOption> ActionTypes { get; } = new[]
    {
        new RuleActionOption { Type = RuleActionType.Move, Label = Translator.Rules_Action_Move },
        new RuleActionOption { Type = RuleActionType.Copy, Label = Translator.Rules_Action_Copy },
        new RuleActionOption { Type = RuleActionType.Categorize, Label = Translator.Rules_Action_Categorize },
        new RuleActionOption { Type = RuleActionType.MarkRead, Label = Translator.Rules_Action_MarkRead },
        new RuleActionOption { Type = RuleActionType.Forward, Label = Translator.Rules_Action_Forward },
        new RuleActionOption { Type = RuleActionType.Delete, Label = Translator.Rules_Action_Delete }
    };

    public static string DefaultFor(RuleValueKind kind, IReadOnlyList<MailItemFolder>? folders, IReadOnlyList<MailCategory>? categories) => kind switch
    {
        RuleValueKind.Folder => folders?.FirstOrDefault()?.RemoteFolderId ?? string.Empty,
        RuleValueKind.Category => categories?.FirstOrDefault()?.Name ?? string.Empty,
        RuleValueKind.Importance => Translator.Rules_Importance_High,
        _ => string.Empty
    };

    public static string FieldLabel(RuleConditionField field)
        => ConditionFields.FirstOrDefault(f => f.Field == field)?.Label ?? field.ToString();

    public static string ActionLabel(RuleActionType type)
        => ActionTypes.FirstOrDefault(a => a.Type == type)?.Label ?? type.ToString();

    /// <summary>"When [conditions], [actions]." Resolves folder/category ids to display names.</summary>
    public static string BuildSummary(RemoteInboxRule rule, IReadOnlyList<MailItemFolder> folders, IReadOnlyList<MailCategory> categories)
    {
        string conditions = rule.Conditions.Count > 0
            ? string.Join($" {Translator.Rules_Summary_And} ", rule.Conditions.Select(DescribeCondition))
            : Translator.Rules_Summary_AnyMessage;

        string actions = rule.Actions.Count > 0
            ? string.Join($", {Translator.Rules_Summary_Then} ", rule.Actions.Select(a => DescribeAction(a, folders, categories)))
            : Translator.Rules_Summary_DoNothing;

        return string.Format(Translator.Rules_Summary_Template, conditions, actions);
    }

    private static string DescribeCondition(RuleConditionModel condition)
    {
        var label = FieldLabel(condition.Field).ToLowerInvariant();
        return RuleFieldCatalog.ValueKind(condition.Field) == RuleValueKind.None
            ? label
            : $"{label} “{Display(condition.Value)}”";
    }

    private static string DescribeAction(RuleActionModel action, IReadOnlyList<MailItemFolder> folders, IReadOnlyList<MailCategory> categories)
    {
        var label = ActionLabel(action.Type).ToLowerInvariant();
        if (RuleFieldCatalog.ValueKind(action.Type) == RuleValueKind.None)
            return label;

        var shown = action.Type is RuleActionType.Move or RuleActionType.Copy
            ? FolderName(action.Value, folders)
            : Display(action.Value);

        return $"{label} “{shown}”";
    }

    private static string FolderName(string? remoteId, IReadOnlyList<MailItemFolder> folders)
        => folders?.FirstOrDefault(f => f.RemoteFolderId == remoteId)?.FolderName ?? Display(remoteId);

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "…" : value;
}
