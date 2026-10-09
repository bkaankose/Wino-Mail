using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Views.Shell;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Condition and action rows of the mail filter editor, and the glyph pop-ups they use.</summary>
public sealed partial class MailFilterEditorPageViewController
{
    private const double FieldWidth = 170;
    private const double OperatorWidth = 150;
    private const double ActionWidth = 200;
    private const double FolderWidth = 240;

    private readonly NSStackView _conditionRows = RowStack();
    private readonly NSStackView _actionRows = RowStack();
    private BindingScope? _conditionScope;
    private BindingScope? _actionScope;
    private bool _conditionsQueued;
    private bool _actionsQueued;

    private static NSStackView RowStack()
    {
        var stack = WinoLayout.VStack(WinoStyle.Space1);
        stack.Alignment = NSLayoutAttribute.Leading;
        return stack;
    }

    /// <summary>Coalesces collection changes into one rebuild after the current event (a pop-up may still be dispatching).</summary>
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

    private static void Clear(NSStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews)
        {
            stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
    }

    private static void AddFullWidth(NSStackView stack, NSView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        stack.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
    }

    private void RebuildConditions()
    {
        _conditionScope?.Dispose();
        _conditionScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_conditionScope);
        Clear(_conditionRows);
        var items = ViewModel.Conditions.ToList();
        foreach (var item in items)
            AddFullWidth(_conditionRows, ConditionRow(rows, item, items.Count > 1));
    }

    private void RebuildActions()
    {
        _actionScope?.Dispose();
        _actionScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_actionScope);
        Clear(_actionRows);
        var items = ViewModel.Actions.ToList();
        foreach (var item in items)
            AddFullWidth(_actionRows, ActionRow(rows, item, items.Count > 1));
    }

    /// <summary>
    /// The conjunction label (AND / OR, from the second row on) above one condition: field glyph tile,
    /// field and operator pop-ups, then a value field or, for attachments and importance, a choice pop-up.
    /// </summary>
    private NSView ConditionRow(SettingsBinder rows, MailFilterConditionEditorItem item, bool canRemove)
    {
        var (tile, icon) = MailFilterPresentation.Tile(MailFilterPresentation.FieldGlyph(item.SelectedField?.Value), WinoStyle.Accent);
        rows.Bind(item, nameof(item.SelectedField), s => s.SelectedField, field => icon.Icon = MailFilterPresentation.FieldGlyph(field?.Value));

        var field = GlyphPopUp(rows, item, s => s.FieldOptions, null, option => option.DisplayName, option => MailFilterPresentation.FieldGlyph(option.Value),
            nameof(item.SelectedField), s => s.SelectedField, (s, v) => s.SelectedField = v, FieldWidth);
        WinoAccessibility.Label(field, Translator.MailFilterEditor_Conditions);

        var op = rows.PopUp(item, s => s.AvailableOperators, option => option.DisplayName, nameof(item.SelectedOperator),
            s => s.SelectedOperator, (s, v) => s.SelectedOperator = v, itemsProperty: nameof(item.AvailableOperators), width: OperatorWidth);
        op.WidthAnchor.ConstraintEqualTo((nfloat)OperatorWidth).Active = true;

        var value = rows.TextField(item, nameof(item.Value), s => s.Value, (s, v) => s.Value = v, null, 0);
        value.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        value.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        WinoAccessibility.Label(value, item.SelectedField?.DisplayName ?? Translator.MailFilterEditor_Conditions);
        rows.Visible(value, item, nameof(item.IsTextField), s => s.IsTextField);

        var choice = rows.PopUp(item, s => s.ChoiceOptions, option => option.DisplayName, nameof(item.SelectedChoice),
            s => s.SelectedChoice, (s, v) => s.SelectedChoice = v, itemsProperty: nameof(item.ChoiceOptions), width: 100);
        choice.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        rows.Visible(choice, item, nameof(item.IsChoiceField), s => s.IsChoiceField);

        var remove = rows.Button(string.Empty, () => ViewModel.RemoveCondition(item), icon: WinoIconGlyph.Delete);
        remove.Enabled = canRemove;
        remove.ToolTip = Translator.MailFilterEditor_Remove;
        WinoAccessibility.Label(remove, Translator.MailFilterEditor_Remove);

        var line = WinoLayout.HStack(WinoStyle.Space2, tile, field, op, value, choice, remove);
        line.Alignment = NSLayoutAttribute.CenterY;

        var conjunction = rows.Label(item, nameof(item.ConjunctionText), s => s.ConjunctionText, NSFont.SystemFontOfSize(11, NSFontWeight.Bold), WinoStyle.TertiaryText);
        var conjunctionHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(conjunction, conjunctionHost, 4, 40, 4, 0);
        rows.Visible(conjunctionHost, item, nameof(item.ShowConjunction), s => s.ShowConjunction);

        var stack = WinoLayout.VStack(0, conjunctionHost, line);
        stack.Alignment = NSLayoutAttribute.Leading;
        line.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        conjunctionHost.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    /// <summary>
    /// One action: tone tile, action pop-up, the destination folder for Move, Remove, and the
    /// permanent-delete warning under the row.
    /// </summary>
    private NSView ActionRow(SettingsBinder rows, MailFilterActionEditorItem item, bool canRemove)
    {
        var (tile, icon) = MailFilterPresentation.Tile(MailFilterPresentation.ActionGlyph(item.SelectedAction?.Value), MailFilterPresentation.ActionTone(item.SelectedAction?.Value));
        rows.Bind(item, nameof(item.SelectedAction), s => s.SelectedAction, action =>
        {
            var tone = MailFilterPresentation.ActionTone(action?.Value);
            tile.Fill = tone.ColorWithAlphaComponent(0.12f);
            icon.Tint = tone;
            icon.Icon = MailFilterPresentation.ActionGlyph(action?.Value);
        });

        var action = GlyphPopUp(rows, item, s => s.ActionOptions, null, option => option.DisplayName, option => MailFilterPresentation.ActionGlyph(option.Value),
            nameof(item.SelectedAction), s => s.SelectedAction, (s, v) => s.SelectedAction = v, ActionWidth);
        WinoAccessibility.Label(action, Translator.MailFilterEditor_Actions);

        var folder = FolderPopUp(rows, item, s => s.FolderOptions, null, nameof(item.SelectedTargetFolder), s => s.SelectedTargetFolder, (s, v) => s.SelectedTargetFolder = v, FolderWidth);
        WinoAccessibility.Label(folder, Translator.MailFilterEditor_TargetFolder);
        rows.Visible(folder, item, nameof(item.NeedsTargetFolder), s => s.NeedsTargetFolder);

        var remove = rows.Button(string.Empty, () => ViewModel.RemoveAction(item), icon: WinoIconGlyph.Delete);
        remove.Enabled = canRemove;
        remove.ToolTip = Translator.MailFilterEditor_Remove;
        WinoAccessibility.Label(remove, Translator.MailFilterEditor_Remove);

        var line = WinoLayout.HStack(WinoStyle.Space2, tile, action, folder, WinoLayout.Spacer(), remove);
        line.Alignment = NSLayoutAttribute.CenterY;

        var warningIcon = new WinoIconView(WinoIconGlyph.Warning, 12, WinoStyle.Critical) { Colorful = false };
        WinoLayout.Size(warningIcon, 12, 12);
        var warningText = WinoStyle.Label(Translator.MailFilterEditor_HardDeleteWarning, WinoStyle.Caption, WinoStyle.Critical, 0);
        warningText.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var warning = WinoLayout.HStack(6, warningIcon, warningText);
        warning.Alignment = NSLayoutAttribute.CenterY;
        var warningHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(warning, warningHost, 4, 40, 0, 0);
        rows.Visible(warningHost, item, nameof(item.IsDestructive), s => s.IsDestructive);

        var stack = WinoLayout.VStack(0, line, warningHost);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(0, 0, 4, 0);
        line.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        warningHost.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    /// <summary>Same rule as the ViewModel: on Wino filters these actions cannot be combined with others.</summary>
    private static bool IsExclusive(MailFilterActionType? action)
        => action is MailFilterActionType.Move
            or MailFilterActionType.Archive
            or MailFilterActionType.MoveToJunk
            or MailFilterActionType.MarkAsNotJunk
            or MailFilterActionType.SoftDelete
            or MailFilterActionType.HardDelete;

    private static WinoIconGlyph FolderGlyph(SpecialFolderType type)
    {
        var glyph = ShellPaneRows.FolderGlyph(type);
        return glyph == WinoIconGlyph.None ? WinoIconGlyph.Folder : glyph;
    }

    private static NSPopUpButton FolderPopUp<T>(SettingsBinder binder, T source, Func<T, IReadOnlyList<MailFilterFolderOption>?> items, INotifyCollectionChanged? collection,
        string property, Func<T, MailFilterFolderOption?> read, Action<T, MailFilterFolderOption> write, double width)
        where T : INotifyPropertyChanged
        => GlyphPopUp(binder, source, items, collection, option => option.DisplayName, option => FolderGlyph(option.SpecialFolderType), property, read, write, width);

    /// <summary>
    /// An NSPopUpButton whose items carry a Wino glyph (the Windows ComboBox item templates), bound to a
    /// selected-item property. With <paramref name="collection"/> the menu follows that collection.
    /// </summary>
    private static NSPopUpButton GlyphPopUp<T, TItem>(SettingsBinder binder, T source, Func<T, IReadOnlyList<TItem>?> items, INotifyCollectionChanged? collection,
        Func<TItem, string> text, Func<TItem, WinoIconGlyph> glyph, string property, Func<T, TItem?> read, Action<T, TItem> write, double width)
        where T : INotifyPropertyChanged where TItem : class
    {
        var popup = new NSPopUpButton(new CGRect(0, 0, width, 24), false)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            ControlSize = NSControlSize.Small,
            Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize),
            AutoEnablesItems = false
        };
        popup.WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
        var current = new List<TItem>();

        void Select(TItem? value)
        {
            var index = value is null ? -1 : current.FindIndex(item => Equals(item, value));
            popup.SelectItem((nint)index);
        }

        void Rebuild()
        {
            current.Clear();
            popup.Menu!.RemoveAllItems();
            foreach (var value in items(source) ?? Array.Empty<TItem>())
            {
                current.Add(value);
                var glyphValue = glyph(value);
                var menuItem = new NSMenuItem(text(value) ?? string.Empty);
                if (glyphValue != WinoIconGlyph.None) menuItem.Image = WinoIcons.Image(glyphValue, 14);
                popup.Menu.AddItem(menuItem);
            }
            Select(read(source));
        }

        if (collection is not null) binder.Collection(collection, Rebuild);
        else Rebuild();
        binder.Bind(source, property, read, Select);
        binder.OnActivated(popup, () =>
        {
            var index = (int)popup.IndexOfSelectedItem;
            if (index >= 0 && index < current.Count && !Equals(read(source), current[index])) write(source, current[index]);
            Select(read(source));
        });
        return popup;
    }
}

/// <summary>
/// One "Runs on" choice: a 40pt accent tile, the title and its explanation, and a checkmark when
/// selected. The selected card has a 2pt accent outline. Click, Return, Space or VoiceOver press selects it.
/// </summary>
internal sealed class MailFilterManagementOptionView : NSView
{
    private readonly WinoSurfaceView _surface;
    private readonly WinoIconView _check;
    private bool _isSelected;
    private bool _isEnabled = true;
    private bool _pressed;

    public MailFilterManagementOptionView(WinoIconGlyph glyph, string title, string description)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _surface = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, CornerRadius = WinoStyle.GroupRadius, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_surface, this);

        var (tile, _) = MailFilterPresentation.Tile(glyph, WinoStyle.Accent, 40, 20);
        var titleLabel = WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        var descriptionLabel = WinoStyle.Label(description, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var text = WinoLayout.VStack(2, titleLabel, descriptionLabel);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        titleLabel.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
        descriptionLabel.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;

        _check = new WinoIconView(WinoIconGlyph.Checkmark, 16, WinoStyle.Accent) { Colorful = false };
        WinoLayout.Size(_check, 16, 16);

        var row = WinoLayout.HStack(WinoStyle.Space3, tile, text, _check);
        row.Alignment = NSLayoutAttribute.Top;
        // Fill gives the text column the card's spare width; gravity areas left it wrapping narrowly.
        row.Distribution = NSStackViewDistribution.Fill;
        WinoLayout.Fill(row, _surface, 12, 14, 12, 14);

        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.RadioButtonRole;
        AccessibilityLabel = title;
        AccessibilityHelp = description;
        Apply();
    }

    public event EventHandler? Activated;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Apply(); }
    }

    /// <summary>Editing an existing filter fixes where it runs (Windows CanChangeManagementType).</summary>
    public bool IsOptionEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; Apply(); }
    }

    public override bool AcceptsFirstResponder() => _isEnabled;
    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override void MouseDown(NSEvent theEvent)
    {
        if (!_isEnabled) { base.MouseDown(theEvent); return; }
        _pressed = true;
        AlphaValue = 0.8f;
    }

    public override void MouseUp(NSEvent theEvent)
    {
        if (!_pressed) { base.MouseUp(theEvent); return; }
        _pressed = false;
        Apply();
        if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Activated?.Invoke(this, EventArgs.Empty);
    }

    public override void KeyDown(NSEvent theEvent)
    {
        if (_isEnabled && theEvent.Characters is "\r" or " ") { Activated?.Invoke(this, EventArgs.Empty); return; }
        base.KeyDown(theEvent);
    }

    public override bool AccessibilityPerformPress()
    {
        if (!_isEnabled) return false;
        Activated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Apply();
    }

    private void Apply()
    {
        _surface.Stroke = _isSelected ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
        _surface.StrokeWidth = _isSelected ? 2 : 1;
        _check.Hidden = !_isSelected;
        AlphaValue = _isEnabled ? 1 : 0.55f;
        AccessibilityValue = new Foundation.NSNumber(_isSelected ? 1 : 0);
    }
}
