using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

// The WHEN and THEN blocks: one custom row per condition and action, rebuilt once per main-loop turn when the
// ViewModel's collections change (an exclusive action removes the others while its drop-down signal is on the stack).
internal sealed partial class MailFilterEditorPage
{
    private Gtk.Entry? _nameEntry;
    private Adw.ComboRow? _sourceRow;
    private Gtk.DropDown? _matchDropDown;
    private WinoSettingsGroup? _when;
    private WinoSettingsGroup? _then;
    private Adw.ButtonRow? _addCondition;
    private Adw.ButtonRow? _addAction;
    private Adw.PreferencesRow? _hintRow;
    private Gtk.Label? _hint;
    private SettingsBinder? _conditionRows;
    private SettingsBinder? _actionRows;
    private readonly List<Gtk.Widget> _conditionWidgets = [];
    private readonly List<Gtk.Widget> _actionWidgets = [];
    private readonly List<ConditionControls> _conditions = [];
    private readonly List<ActionControls> _actions = [];
    private bool _focusNewCondition;
    private bool _focusNewAction;

    private void BuildWhenBlock()
    {
        var vm = ViewModel;
        _when = Page.AddGroup($"{Translator.MailFilterEditor_WhenBadge} · {Translator.MailFilterEditor_WhenTitle}");
        _when.HidesWhenEmpty = false;
        _matchDropDown = Settings.ChoiceSuffix(vm, Translator.MailFilterEditor_Match,
            (string[])[Translator.MailFilterEditor_MatchAll, Translator.MailFilterEditor_MatchAny],
            nameof(vm.SelectedMatchModeIndex), static s => s.SelectedMatchModeIndex, static (s, v) => s.SelectedMatchModeIndex = v);
        _when.HeaderSuffix = _matchDropDown;
        _addCondition = WinoSettingsRows.Button(Translator.MailFilterEditor_AddCondition);
        _addCondition.SetStartIconName("list-add-symbolic");
        Settings.Own(GtkSignals.Connect<Adw.ButtonRow>(h => _addCondition.OnActivated += h, h => _addCondition.OnActivated -= h, (_, _) =>
        {
            _focusNewCondition = true;
            ViewModel.AddCondition();
        }));
        _when.Add(_addCondition);
        Settings.Collection(vm.Conditions, RebuildConditions);
    }

    private void BuildThenBlock()
    {
        var vm = ViewModel;
        _then = Page.AddGroup($"{Translator.MailFilterEditor_ThenBadge} · {Translator.MailFilterEditor_ThenTitle}");
        _then.HidesWhenEmpty = false;
        var notice = WinoSettingsRows.Action(Translator.MailFilterEditor_ExclusiveActionTitle, Translator.MailFilterEditor_ExclusiveActionDescription, WinoIconGlyph.Info);
        Settings.Visible(notice, vm, nameof(vm.IsWinoManaged), static s => s.IsWinoManaged);
        _then.Add(notice);
        _addAction = WinoSettingsRows.Button(Translator.MailFilterEditor_AddAction);
        _addAction.SetStartIconName("list-add-symbolic");
        Settings.Own(GtkSignals.Connect<Adw.ButtonRow>(h => _addAction.OnActivated += h, h => _addAction.OnActivated -= h, (_, _) =>
        {
            _focusNewAction = true;
            ViewModel.AddAction();
        }));
        _then.Add(_addAction);
        _hint = Gtk.Label.New(null);
        _hint.SetWrap(true);
        _hint.SetXalign(0);
        _hint.AddCssClass("dim-label");
        _hint.AddCssClass("caption");
        _hint.SetMarginTop(8);
        _hint.SetMarginBottom(8);
        _hint.SetMarginStart(12);
        _hint.SetMarginEnd(12);
        _hintRow = WinoSettingsRows.Custom(_hint);
        _hintRow.SetVisible(false);
        _then.Add(_hintRow);
        Settings.Collection(vm.Actions, RebuildActions);
    }

    private void RebuildConditions()
    {
        if (_when is null || _addCondition is null || IsReleased) return;
        _conditionRows?.Scope.Dispose();
        foreach (var widget in _conditionWidgets) _when.Remove(widget);
        _conditionWidgets.Clear();
        _conditions.Clear();
        _when.Remove(_addCondition);
        var rows = _conditionRows = Settings.Child();
        var items = ViewModel.Conditions.ToList();
        for (var index = 0; index < items.Count; index++)
        {
            var row = ConditionRow(rows, items[index], index);
            _when.Add(row);
            _conditionWidgets.Add(row);
        }
        _when.Add(_addCondition);
        if (_focusNewCondition && _conditions.Count > 0)
        {
            _focusNewCondition = false;
            var last = _conditions[^1];
            MainLoop.Post(() => (last.Value.GetVisible() ? (Gtk.Widget)last.Value : last.Field).GrabFocus());
        }
    }

    private void RebuildActions()
    {
        if (_then is null || _addAction is null || _hintRow is null || IsReleased) return;
        _actionRows?.Scope.Dispose();
        foreach (var widget in _actionWidgets) _then.Remove(widget);
        _actionWidgets.Clear();
        _actions.Clear();
        _then.Remove(_addAction);
        _then.Remove(_hintRow);
        var rows = _actionRows = Settings.Child();
        var items = ViewModel.Actions.ToList();
        for (var index = 0; index < items.Count; index++)
        {
            var row = ActionRow(rows, items[index], index);
            _then.Add(row);
            _actionWidgets.Add(row);
        }
        _then.Add(_addAction);
        _then.Add(_hintRow);
        RefreshAddAction();
        if (_focusNewAction && _actions.Count > 0)
        {
            _focusNewAction = false;
            var last = _actions[^1];
            MainLoop.Post(() => last.Action.GrabFocus());
        }
    }

    private Adw.PreferencesRow ConditionRow(SettingsBinder rows, MailFilterConditionEditorItem item, int index)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        box.SetMarginTop(8);
        box.SetMarginBottom(8);
        box.SetMarginStart(12);
        box.SetMarginEnd(8);

        var conjunction = Gtk.Label.New(null);
        conjunction.AddCssClass("caption-heading");
        conjunction.AddCssClass("dim-label");
        conjunction.SetXalign(0);
        rows.Bind(item, nameof(item.ConjunctionText), static s => s.ConjunctionText, text => conjunction.SetText(text ?? string.Empty));
        rows.Visible(conjunction, item, nameof(item.ShowConjunction), static s => s.ShowConjunction);
        box.Append(conjunction);

        var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var tile = Adw.Bin.New();
        tile.SetValign(Gtk.Align.Center);
        rows.Bind(item, nameof(item.SelectedField), static s => s.SelectedField, field =>
            tile.SetChild(MailFilterPresentation.Tile(MailFilterDisplay.GetFieldGlyph(field?.Value), "accent")));
        line.Append(tile);

        var wrap = Adw.WrapBox.New();
        wrap.SetChildSpacing(6);
        wrap.SetLineSpacing(6);
        wrap.SetHexpand(true);
        var number = index + 1;
        var field = rows.ChoiceSuffix<MailFilterConditionEditorItem, MailFilterConditionFieldOption>(item, $"{Translator.MailFilterEditor_Conditions} {number}",
            static s => s.FieldOptions, static option => option.DisplayName ?? string.Empty,
            nameof(item.SelectedField), static s => s.SelectedField, static (s, v) => s.SelectedField = v);
        var op = rows.ChoiceSuffix<MailFilterConditionEditorItem, MailFilterConditionOperatorOption>(item, $"{Translator.MailFilterEditor_Match} {number}",
            static s => s.AvailableOperators, static option => option.DisplayName ?? string.Empty,
            nameof(item.SelectedOperator), static s => s.SelectedOperator, static (s, v) => s.SelectedOperator = v,
            itemsProperty: nameof(item.AvailableOperators));
        var value = rows.TextSuffix(item, item.SelectedField?.DisplayName ?? Translator.MailFilterEditor_Conditions, null,
            nameof(item.Value), static s => s.Value, static (s, v) => s.Value = v, 18);
        value.SetHexpand(true);
        rows.Visible(value, item, nameof(item.IsTextField), static s => s.IsTextField);
        rows.Bind(item, nameof(item.SelectedField), static s => s.SelectedField, selected =>
            WinoA11y.Label(value, $"{selected?.DisplayName ?? Translator.MailFilterEditor_Conditions} {number}"));
        var choice = rows.ChoiceSuffix<MailFilterConditionEditorItem, MailFilterValueChoiceOption>(item, $"{Translator.MailFilterEditor_Conditions} {number} {Translator.MailFilterEditor_Match}",
            static s => s.ChoiceOptions, static option => option.DisplayName ?? string.Empty,
            nameof(item.SelectedChoice), static s => s.SelectedChoice, static (s, v) => s.SelectedChoice = v,
            itemsProperty: nameof(item.ChoiceOptions));
        rows.Visible(choice, item, nameof(item.IsChoiceField), static s => s.IsChoiceField);
        wrap.Append(field);
        wrap.Append(op);
        wrap.Append(value);
        wrap.Append(choice);
        line.Append(wrap);

        var remove = WinoSettingsRows.GlyphButton(WinoIconGlyph.Delete, $"{Translator.MailFilterEditor_Remove} {number}");
        remove.SetTooltipText(Translator.MailFilterEditor_Remove);
        rows.Own(GtkSignals.OnClicked(remove, () => ViewModel.RemoveCondition(item)));
        line.Append(remove);
        box.Append(line);

        _conditions.Add(new ConditionControls(item, field, op, value, choice));
        return WinoSettingsRows.Custom(box, $"{Translator.MailFilterEditor_Conditions} {number}");
    }

    private Adw.PreferencesRow ActionRow(SettingsBinder rows, MailFilterActionEditorItem item, int index)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        box.SetMarginTop(8);
        box.SetMarginBottom(8);
        box.SetMarginStart(12);
        box.SetMarginEnd(8);

        var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var tile = Adw.Bin.New();
        tile.SetValign(Gtk.Align.Center);
        rows.Bind(item, nameof(item.SelectedAction), static s => s.SelectedAction, selected =>
        {
            tile.SetChild(MailFilterPresentation.Tile(MailFilterDisplay.GetActionGlyph(selected?.Value), MailFilterPresentation.ToneClass(selected?.Value)));
            RefreshAddAction();
        });
        line.Append(tile);

        var wrap = Adw.WrapBox.New();
        wrap.SetChildSpacing(6);
        wrap.SetLineSpacing(6);
        wrap.SetHexpand(true);
        var number = index + 1;
        var action = rows.ChoiceSuffix<MailFilterActionEditorItem, MailFilterActionOption>(item, $"{Translator.MailFilterEditor_Actions} {number}",
            static s => s.ActionOptions, static option => option.DisplayName ?? string.Empty,
            nameof(item.SelectedAction), static s => s.SelectedAction, static (s, v) => s.SelectedAction = v);
        var folder = rows.ChoiceSuffix<MailFilterActionEditorItem, MailFilterFolderOption>(item, Translator.MailFilterEditor_TargetFolder,
            static s => s.FolderOptions, static option => option.DisplayName ?? string.Empty,
            nameof(item.SelectedTargetFolder), static s => s.SelectedTargetFolder, static (s, v) => s.SelectedTargetFolder = v,
            itemsCollection: item.FolderOptions);
        rows.Visible(folder, item, nameof(item.NeedsTargetFolder), static s => s.NeedsTargetFolder);
        wrap.Append(action);
        wrap.Append(folder);
        line.Append(wrap);

        var remove = WinoSettingsRows.GlyphButton(WinoIconGlyph.Delete, $"{Translator.MailFilterEditor_Remove} {Translator.MailFilterEditor_Actions} {number}");
        remove.SetTooltipText(Translator.MailFilterEditor_Remove);
        rows.Own(GtkSignals.OnClicked(remove, () => ViewModel.RemoveAction(item)));
        line.Append(remove);
        box.Append(line);

        var warning = Gtk.Label.New(Translator.MailFilterEditor_HardDeleteWarning);
        warning.SetWrap(true);
        warning.SetXalign(0);
        warning.AddCssClass("error");
        warning.AddCssClass("caption");
        rows.Visible(warning, item, nameof(item.IsDestructive), static s => s.IsDestructive);
        box.Append(warning);

        _actions.Add(new ActionControls(item, action, folder, warning));
        return WinoSettingsRows.Custom(box, $"{Translator.MailFilterEditor_Actions} {number}");
    }

    private void RefreshAddAction()
    {
        if (_addAction is null || _hint is null || _hintRow is null) return;
        var canAdd = ViewModel.CanAddAction;
        _addAction.SetSensitive(canAdd);
        var exclusive = ViewModel.Actions.FirstOrDefault(action => MailFilterDisplay.IsExclusiveAction(action.SelectedAction?.Value));
        var showHint = !canAdd && exclusive?.SelectedAction is not null;
        _hint.SetText(showHint && exclusive?.SelectedAction is { } option ? string.Format(Translator.MailFilterEditor_ExclusiveActionHint, option.DisplayName) : string.Empty);
        _hintRow.SetVisible(showHint);
    }

    private sealed record ConditionControls(MailFilterConditionEditorItem Item, Gtk.DropDown Field, Gtk.DropDown Operator, Gtk.Entry Value, Gtk.DropDown Choice);

    private sealed record ActionControls(MailFilterActionEditorItem Item, Gtk.DropDown Action, Gtk.DropDown Folder, Gtk.Label Warning);
}
