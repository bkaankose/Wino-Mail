using System.Collections.Specialized;
using System.ComponentModel;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Condition and action rows of the mail filter editor (iOS MailFilterEditorPageViewController.Rows). At phone width
/// each row takes two lines: the glyph tile, the main dropdown and Remove, then the operator and value (conditions) or
/// the target folder. Dropdowns list text only; the row's tile shows the selected field or action glyph.
/// </summary>
public sealed partial class MailFilterEditorPageFragment
{
    private LinearLayout? _conditionRows;
    private LinearLayout? _actionRows;
    private BindingScope? _conditionScope;
    private BindingScope? _actionScope;
    private bool _conditionsQueued;
    private bool _actionsQueued;

    /// <summary>Coalesces collection changes into one rebuild after the current event (a menu may still be dispatching).</summary>
    private void QueueConditionsRebuild()
    {
        if (_conditionsQueued) return;
        _conditionsQueued = true;
        Page.Post(() =>
        {
            _conditionsQueued = false;
            if (!Bindings.IsDisposed) RebuildConditions();
        });
    }

    private void QueueActionsRebuild()
    {
        if (_actionsQueued) return;
        _actionsQueued = true;
        Page.Post(() =>
        {
            _actionsQueued = false;
            if (!Bindings.IsDisposed) RebuildActions();
        });
    }

    private void RebuildConditions()
    {
        if (_conditionRows is null) return;
        _conditionScope?.Dispose();
        _conditionScope = new BindingScope();
        var rows = Bind.Child(_conditionScope);
        _conditionRows.RemoveAllViews();
        var items = VM.Conditions.ToList();
        foreach (var item in items) AccountSettingsParts.AddStretched(_conditionRows, ConditionRow(rows, item, items.Count > 1), Resource.Dimension.wino_space_s);
    }

    private void RebuildActions()
    {
        if (_actionRows is null) return;
        _actionScope?.Dispose();
        _actionScope = new BindingScope();
        var rows = Bind.Child(_actionScope);
        _actionRows.RemoveAllViews();
        var items = VM.Actions.ToList();
        foreach (var item in items) AccountSettingsParts.AddStretched(_actionRows, ActionRow(rows, item, items.Count > 1), Resource.Dimension.wino_space_s);
    }

    private MaterialButton RemoveButton(SettingsBinder rows, bool canRemove, Action remove)
    {
        var button = AccountSettingsParts.GlyphButton(Ctx, WinoIconGlyph.Delete, Translator.MailFilterEditor_Remove);
        button.Enabled = canRemove;
        rows.Views.Click(button, remove);
        return button;
    }

    /// <summary>The first line of a row: tile, the main dropdown filling the width, and Remove.</summary>
    private LinearLayout Line(View tile, View main, View remove)
    {
        var line = AccountSettingsParts.HRow(Ctx);
        line.AddView(tile);
        line.AddView(main, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_s));
        line.AddView(remove, AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_2xs));
        return line;
    }

    /// <summary>The start inset of a row's second line: the tile width plus its gap, so it lines up with the dropdown.</summary>
    private int DetailIndent()
        => WinoTokens.Px(Ctx, Resource.Dimension.wino_size_swatch_large) + WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s);

    /// <summary>
    /// The conjunction label (AND / OR, from the second row on) above one condition: field glyph tile, field dropdown and
    /// Remove, then the operator dropdown with a value field or, for attachments and importance, a choice dropdown.
    /// </summary>
    private View ConditionRow(SettingsBinder rows, MailFilterConditionEditorItem item, bool canRemove)
    {
        var (tile, icon) = AccountSettingsParts.Tile(Ctx, MailFilterDisplay.GetFieldGlyph(item.SelectedField?.Value), WinoColors.Accent(Ctx));
        rows.Bind(item, nameof(item.SelectedField), s => s.SelectedField, field => icon.Icon = MailFilterDisplay.GetFieldGlyph(field?.Value));

        var field = rows.PopUp(item, s => s.FieldOptions, option => option.DisplayName, nameof(item.SelectedField), s => s.SelectedField, (s, v) => s.SelectedField = v);
        field.ContentDescription = Translator.MailFilterEditor_Conditions;

        var op = rows.PopUp(item, s => s.AvailableOperators, option => option.DisplayName, nameof(item.SelectedOperator),
            s => s.SelectedOperator, (s, v) => s.SelectedOperator = v, itemsProperty: nameof(item.AvailableOperators));

        var value = rows.TextField(item, nameof(item.Value), s => s.Value, (s, v) => s.Value = v);
        value.ContentDescription = item.SelectedField?.DisplayName ?? Translator.MailFilterEditor_Conditions;
        rows.Visible(value, item, nameof(item.IsTextField), s => s.IsTextField);

        var choice = rows.PopUp(item, s => s.ChoiceOptions, option => option.DisplayName, nameof(item.SelectedChoice),
            s => s.SelectedChoice, (s, v) => s.SelectedChoice = v, itemsProperty: nameof(item.ChoiceOptions));
        rows.Visible(choice, item, nameof(item.IsChoiceField), s => s.IsChoiceField);

        var remove = RemoveButton(rows, canRemove, () => VM.RemoveCondition(item));

        var detail = AccountSettingsParts.HRow(Ctx);
        detail.AddView(op);
        detail.AddView(value, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_s));
        detail.AddView(choice, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_s));
        detail.SetPaddingRelative(DetailIndent(), 0, 0, 0);

        var conjunction = rows.Label(item, nameof(item.ConjunctionText), s => s.ConjunctionText, WinoTextStyle.CaptionStrong);
        conjunction.SetTextColor(WinoColors.TertiaryText(Ctx));
        var gap = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs);
        conjunction.SetPaddingRelative(DetailIndent(), gap, 0, gap);
        rows.Visible(conjunction, item, nameof(item.ShowConjunction), s => s.ShowConjunction);

        var stack = Stack(Resource.Dimension.wino_space_2xs, conjunction, Line(tile, field, remove), detail);
        return stack;
    }

    /// <summary>One action: tone tile, action dropdown and Remove, the destination folder for Move, and the permanent-delete warning.</summary>
    private View ActionRow(SettingsBinder rows, MailFilterActionEditorItem item, bool canRemove)
    {
        var (tile, icon) = AccountSettingsParts.Tile(Ctx, MailFilterDisplay.GetActionGlyph(item.SelectedAction?.Value), AccountSettingsParts.ActionTone(Ctx, item.SelectedAction?.Value));
        rows.Bind(item, nameof(item.SelectedAction), s => s.SelectedAction, action =>
            AccountSettingsParts.SetTile(Ctx, tile, icon, MailFilterDisplay.GetActionGlyph(action?.Value), AccountSettingsParts.ActionTone(Ctx, action?.Value)));

        var action = rows.PopUp(item, s => s.ActionOptions, option => option.DisplayName, nameof(item.SelectedAction), s => s.SelectedAction, (s, v) => s.SelectedAction = v);
        action.ContentDescription = Translator.MailFilterEditor_Actions;

        var folder = FolderPopUp(rows, item, s => s.FolderOptions, nameof(item.SelectedTargetFolder), s => s.SelectedTargetFolder, (s, v) => s.SelectedTargetFolder = v, item.FolderOptions);
        folder.ContentDescription = Translator.MailFilterEditor_TargetFolder;
        var folderHost = new FrameLayout(Ctx);
        folderHost.SetPaddingRelative(DetailIndent(), 0, 0, 0);
        folderHost.AddView(folder, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        rows.Visible(folderHost, item, nameof(item.NeedsTargetFolder), s => s.NeedsTargetFolder);

        var remove = RemoveButton(rows, canRemove, () => VM.RemoveAction(item));

        var critical = WinoColors.Critical(Ctx);
        var warning = AccountSettingsParts.HRow(Ctx);
        warning.SetGravity(GravityFlags.Top);
        warning.AddView(AccountSettingsParts.Glyph(Ctx, WinoIconGlyph.Warning, Resource.Dimension.wino_glyph_xs, critical, colorful: false));
        warning.AddView(AccountSettingsParts.Label(Ctx, Translator.MailFilterEditor_HardDeleteWarning, WinoTextStyle.Caption, critical),
            AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_xs));
        warning.SetPaddingRelative(DetailIndent(), WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs), 0, 0);
        rows.Visible(warning, item, nameof(item.IsDestructive), s => s.IsDestructive);

        var stack = Stack(Resource.Dimension.wino_space_2xs, Line(tile, action, remove), folderHost, warning);
        WinoTokens.Pad(stack, 0, 0, 0, Resource.Dimension.wino_space_2xs);
        return stack;
    }

    /// <summary>A folder dropdown bound to a selected-folder property; the menu follows <paramref name="collection"/>.</summary>
    private static MaterialButton FolderPopUp<T>(SettingsBinder binder, T source, Func<T, IReadOnlyList<MailFilterFolderOption>?> items,
        string property, Func<T, MailFilterFolderOption?> read, Action<T, MailFilterFolderOption> write, INotifyCollectionChanged? collection)
        where T : INotifyPropertyChanged
        => binder.PopUp(source, items, option => option.DisplayName, property, read, write, itemsCollection: collection);
}
