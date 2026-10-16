using System.Collections.Specialized;
using System.ComponentModel;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Condition and action rows of the mail filter editor, and the glyph menus they use. At phone width each row takes two
/// lines: the glyph tile, the main menu and Remove, then the operator and value (conditions) or the target folder.
/// </summary>
public sealed partial class MailFilterEditorPageViewController
{
    private readonly UIStackView _conditionRows = RowStack();
    private readonly UIStackView _actionRows = RowStack();
    private BindingScope? _conditionScope;
    private BindingScope? _actionScope;
    private bool _conditionsQueued;
    private bool _actionsQueued;

    private static UIStackView RowStack()
    {
        var stack = WinoLayout.VStack(WinoStyle.Space2);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    /// <summary>Coalesces collection changes into one rebuild after the current event (a menu may still be dispatching).</summary>
    private void QueueConditionsRebuild()
    {
        if (_conditionsQueued) return;
        _conditionsQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _conditionsQueued = false;
            if (!Bindings.IsDisposed) RebuildConditions();
        });
    }

    private void QueueActionsRebuild()
    {
        if (_actionsQueued) return;
        _actionsQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _actionsQueued = false;
            if (!Bindings.IsDisposed) RebuildActions();
        });
    }

    private static void Clear(UIStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews)
        {
            stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
    }

    private void RebuildConditions()
    {
        _conditionScope?.Dispose();
        _conditionScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_conditionScope);
        Clear(_conditionRows);
        var items = ViewModel.Conditions.ToList();
        foreach (var item in items) _conditionRows.AddArrangedSubview(ConditionRow(rows, item, items.Count > 1));
    }

    private void RebuildActions()
    {
        _actionScope?.Dispose();
        _actionScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_actionScope);
        Clear(_actionRows);
        var items = ViewModel.Actions.ToList();
        foreach (var item in items) _actionRows.AddArrangedSubview(ActionRow(rows, item, items.Count > 1));
    }

    private SettingsGlyphButton RemoveButton(SettingsBinder rows, bool canRemove, Action remove)
    {
        var button = new SettingsGlyphButton(WinoIconGlyph.Delete, Translator.MailFilterEditor_Remove) { Enabled = canRemove };
        rows.OnActivated(button, remove);
        return button;
    }

    /// <summary>
    /// The conjunction label (AND / OR, from the second row on) above one condition: field glyph tile, field menu and
    /// Remove, then the operator menu with a value field or, for attachments and importance, a choice menu.
    /// </summary>
    private UIView ConditionRow(SettingsBinder rows, MailFilterConditionEditorItem item, bool canRemove)
    {
        var (tile, icon) = MailFilterPresentation.Tile(MailFilterDisplay.GetFieldGlyph(item.SelectedField?.Value), WinoStyle.Accent);
        rows.Bind(item, nameof(item.SelectedField), s => s.SelectedField, field => icon.Icon = MailFilterDisplay.GetFieldGlyph(field?.Value));

        var field = GlyphPopUp(rows, item, s => s.FieldOptions, null, option => option.DisplayName, option => MailFilterDisplay.GetFieldGlyph(option.Value),
            nameof(item.SelectedField), s => s.SelectedField, (s, v) => s.SelectedField = v);
        WinoAccessibility.Label(field, Translator.MailFilterEditor_Conditions);

        var op = rows.PopUp(item, s => s.AvailableOperators, option => option.DisplayName, nameof(item.SelectedOperator),
            s => s.SelectedOperator, (s, v) => s.SelectedOperator = v, itemsProperty: nameof(item.AvailableOperators), width: 110);
        WinoLayout.Priorities(op, UILayoutConstraintAxis.Horizontal, 750, 750);

        var value = rows.TextField(item, nameof(item.Value), s => s.Value, (s, v) => s.Value = v, null, 0);
        WinoLayout.Priorities(value, UILayoutConstraintAxis.Horizontal, 1, 250);
        value.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(value, item.SelectedField?.DisplayName ?? Translator.MailFilterEditor_Conditions);
        rows.Visible(value, item, nameof(item.IsTextField), s => s.IsTextField);

        var choice = rows.PopUp(item, s => s.ChoiceOptions, option => option.DisplayName, nameof(item.SelectedChoice),
            s => s.SelectedChoice, (s, v) => s.SelectedChoice = v, itemsProperty: nameof(item.ChoiceOptions), width: 100);
        WinoLayout.Priorities(choice, UILayoutConstraintAxis.Horizontal, 1, 250);
        rows.Visible(choice, item, nameof(item.IsChoiceField), s => s.IsChoiceField);

        var remove = RemoveButton(rows, canRemove, () => ViewModel.RemoveCondition(item));

        var line = WinoLayout.HStack(WinoStyle.Space2, tile, field, remove);
        var detail = WinoLayout.HStack(WinoStyle.Space2, op, value, choice);
        WinoLayout.Insets(detail, 0, 40, 0, 0);

        var conjunction = rows.Label(item, nameof(item.ConjunctionText), s => s.ConjunctionText, WinoStyle.Scaled(11, UIFontWeight.Bold), WinoStyle.TertiaryText);
        var conjunctionHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(conjunction, conjunctionHost, 4, 40, 4, 0);
        rows.Visible(conjunctionHost, item, nameof(item.ShowConjunction), s => s.ShowConjunction);

        var stack = WinoLayout.VStack(WinoStyle.Space1, conjunctionHost, line, detail);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    /// <summary>One action: tone tile, action menu and Remove, the destination folder for Move, and the permanent-delete warning.</summary>
    private UIView ActionRow(SettingsBinder rows, MailFilterActionEditorItem item, bool canRemove)
    {
        var (tile, icon) = MailFilterPresentation.Tile(MailFilterDisplay.GetActionGlyph(item.SelectedAction?.Value), MailFilterPresentation.ActionTone(item.SelectedAction?.Value));
        rows.Bind(item, nameof(item.SelectedAction), s => s.SelectedAction, action =>
        {
            var tone = MailFilterPresentation.ActionTone(action?.Value);
            tile.Fill = tone.ColorWithAlpha(0.12f);
            icon.Tint = tone;
            icon.Icon = MailFilterDisplay.GetActionGlyph(action?.Value);
        });

        var action = GlyphPopUp(rows, item, s => s.ActionOptions, null, option => option.DisplayName, option => MailFilterDisplay.GetActionGlyph(option.Value),
            nameof(item.SelectedAction), s => s.SelectedAction, (s, v) => s.SelectedAction = v);
        WinoAccessibility.Label(action, Translator.MailFilterEditor_Actions);

        var folder = FolderPopUp(rows, item, s => s.FolderOptions, null, nameof(item.SelectedTargetFolder), s => s.SelectedTargetFolder, (s, v) => s.SelectedTargetFolder = v);
        WinoAccessibility.Label(folder, Translator.MailFilterEditor_TargetFolder);
        var folderHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(folder, folderHost, 0, 40, 0, 0);
        rows.Visible(folderHost, item, nameof(item.NeedsTargetFolder), s => s.NeedsTargetFolder);

        var remove = RemoveButton(rows, canRemove, () => ViewModel.RemoveAction(item));
        var line = WinoLayout.HStack(WinoStyle.Space2, tile, action, remove);

        var warningIcon = new WinoIconView(WinoIconGlyph.Warning, 12, WinoStyle.Critical) { Colorful = false };
        WinoLayout.Size(warningIcon, 12, 12);
        var warningText = WinoStyle.Label(Translator.MailFilterEditor_HardDeleteWarning, WinoStyle.Caption, WinoStyle.Critical, 0);
        WinoLayout.Priorities(warningText, UILayoutConstraintAxis.Horizontal, 1, 250);
        var warning = WinoLayout.HStack(6, warningIcon, warningText);
        var warningHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(warning, warningHost, 4, 40, 0, 0);
        rows.Visible(warningHost, item, nameof(item.IsDestructive), s => s.IsDestructive);

        var stack = WinoLayout.VStack(WinoStyle.Space1, line, folderHost, warningHost);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Insets(stack, 0, 0, 4, 0);
        return stack;
    }

    private static WinoIconGlyph FolderGlyph(SpecialFolderType type) => SpecialFolderGlyphs.GetGlyphOrFolder(type);

    private static UIButton FolderPopUp<T>(SettingsBinder binder, T source, Func<T, IReadOnlyList<MailFilterFolderOption>?> items, INotifyCollectionChanged? collection,
        string property, Func<T, MailFilterFolderOption?> read, Action<T, MailFilterFolderOption> write)
        where T : INotifyPropertyChanged
        => GlyphPopUp(binder, source, items, collection, option => option.DisplayName, option => FolderGlyph(option.SpecialFolderType), property, read, write);

    /// <summary>
    /// A pull-down menu button whose items carry a Wino glyph (the Windows ComboBox item templates), bound to a
    /// selected-item property. It fills the spare row width. With <paramref name="collection"/> the menu follows that collection.
    /// </summary>
    private static UIButton GlyphPopUp<T, TItem>(SettingsBinder binder, T source, Func<T, IReadOnlyList<TItem>?> items, INotifyCollectionChanged? collection,
        Func<TItem, string> text, Func<TItem, WinoIconGlyph> glyph, string property, Func<T, TItem?> read, Action<T, TItem> write)
        where T : INotifyPropertyChanged where TItem : class
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        configuration.ImagePadding = 6;
        var button = new UIButton { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 1, 250);
        var current = new List<TItem>();

        void Render(TItem? selected)
        {
            var actions = new UIMenuElement[current.Count];
            for (int index = 0; index < current.Count; index++)
            {
                var value = current[index];
                var glyphValue = glyph(value);
                var image = glyphValue == WinoIconGlyph.None ? null : WinoIcons.Image(glyphValue, 17);
                var action = UIAction.Create(text(value) ?? string.Empty, image, null, _ =>
                {
                    try
                    {
                        if (!Equals(read(source), value)) write(source, value);
                        Render(read(source));
                    }
                    catch (Exception exception) { binder.Error(exception); }
                });
                action.State = selected is not null && Equals(value, selected) ? UIMenuElementState.On : UIMenuElementState.Off;
                actions[index] = action;
            }
            button.Menu = UIMenu.Create(actions);
            if (button.Configuration is { } shown)
            {
                var index = selected is null ? -1 : current.FindIndex(item => Equals(item, selected));
                shown.Title = index >= 0 ? text(current[index]) ?? string.Empty : string.Empty;
                var selectedGlyph = index >= 0 ? glyph(current[index]) : WinoIconGlyph.None;
                shown.Image = selectedGlyph == WinoIconGlyph.None ? null : WinoIcons.Image(selectedGlyph, 15);
                button.Configuration = shown;
            }
        }

        void Rebuild()
        {
            current.Clear();
            current.AddRange(items(source) ?? []);
            Render(read(source));
        }

        if (collection is not null) binder.Collection(collection, Rebuild);
        else Rebuild();
        binder.Bind(source, property, read, Render);
        return button;
    }
}

/// <summary>
/// One "Runs on" choice (Mac MailFilterManagementOptionView): a 40pt accent tile, the title and its explanation, and a
/// checkmark when selected. The selected card has a 2pt accent outline. A tap or VoiceOver activation selects it.
/// </summary>
internal sealed class MailFilterManagementOptionView : WinoPressableView
{
    private readonly WinoIconView _check;
    private bool _isSelected;

    public MailFilterManagementOptionView(WinoIconGlyph glyph, string title, string description)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Fill = WinoSettingsStyle.GroupFill;
        CornerRadius = WinoStyle.GroupRadius;

        var (tile, _) = MailFilterPresentation.Tile(glyph, WinoStyle.Accent, 40, 20);
        var titleLabel = WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        var descriptionLabel = WinoStyle.Label(description, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var text = WinoLayout.VStack(2, titleLabel, descriptionLabel);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);

        _check = new WinoIconView(WinoIconGlyph.Checkmark, 16, WinoStyle.Accent) { Colorful = false };
        WinoLayout.Size(_check, 16, 16);

        var row = WinoLayout.HStack(WinoStyle.Space3, tile, text, _check);
        row.Alignment = UIStackViewAlignment.Top;
        row.UserInteractionEnabled = false;
        WinoLayout.Fill(row, this, 12, 14, 12, 14);
        HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;

        AccessibilityLabel = title;
        AccessibilityHint = description;
        Apply();
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Apply(); }
    }

    protected override void OnEnabledChanged() => Apply();

    private void Apply()
    {
        Stroke = _isSelected ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
        StrokeWidth = _isSelected ? 2 : 1;
        _check.Hidden = !_isSelected;
        Alpha = Enabled ? 1 : 0.55f;
        var traits = UIAccessibilityTrait.Button;
        if (_isSelected) traits |= UIAccessibilityTrait.Selected;
        if (!Enabled) traits |= UIAccessibilityTrait.NotEnabled;
        AccessibilityTraits = traits;
    }
}
