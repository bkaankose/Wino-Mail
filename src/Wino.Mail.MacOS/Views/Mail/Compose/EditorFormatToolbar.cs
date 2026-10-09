using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Editor.AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail.Compose;

internal enum EditorToolbarLayout
{
    /// <summary>Format and Insert commands (and spell check) in one overflowing row.</summary>
    SingleRow,
    /// <summary>Format / Insert / Options tabs, like the Windows EditorTabbedCommandBarControl.</summary>
    Tabbed
}

/// <summary>Which optional command groups an <see cref="EditorFormatToolbar"/> shows.</summary>
internal sealed record EditorToolbarFeatures(
    bool Highlight = true,
    bool Table = true,
    bool Emoji = true,
    bool ClearFormatting = true,
    bool Paragraph = true,
    bool LineSpacing = true,
    bool Alignment = true,
    bool LinkTools = true,
    bool Image = true,
    bool SpellCheck = true);

/// <summary>
/// The HTML editor formatting toolbar shared by the composer, the signature and template editors and
/// event notes: the Windows EditorTabbedCommandBarControl vocabulary (font, size, B/I/U/S, colours,
/// clear formatting, paragraph style, lists, indent, alignment, line spacing; picture, emoji, table,
/// link, remove link, image properties; spell check and its language) over an
/// <see cref="AppKitHtmlMailEditorSession"/>. Each row overflows into a "More" menu instead of clipping.
/// Hosts add their own commands with <see cref="AddInsertItems"/>, <see cref="AddOptionsItems"/> and
/// <see cref="AddTrailingItem"/>. Main thread only; call <see cref="Detach"/> before disposing the session.
/// </summary>
internal sealed class EditorFormatToolbar : NSView
{
    internal static readonly string[] Fonts = ["Helvetica Neue", "Helvetica", "Arial", "Times New Roman", "Georgia", "Verdana", "Courier New"];
    internal static readonly int[] FontSizes = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 48];
    internal static readonly string[] LineHeights = ["normal", "1", "1.15", "1.5", "2"];

    /// <summary>The Windows editor text colours (WinoMailEditor capabilities); empty means automatic.</summary>
    private static readonly (Func<string> Name, string Hex)[] TextColors =
    [
        (() => Translator.ComposerParity_ColorAutomatic, string.Empty), (() => Translator.ComposerParity_ColorBlack, "#000000"),
        (() => Translator.ComposerParity_ColorGray, "#666666"), (() => Translator.ComposerParity_ColorRed, "#c62828"),
        (() => Translator.ComposerParity_ColorOrange, "#ef6c00"), (() => Translator.ComposerParity_ColorYellow, "#f9a825"),
        (() => Translator.ComposerParity_ColorGreen, "#2e7d32"), (() => Translator.ComposerParity_ColorBlue, "#1565c0"),
        (() => Translator.ComposerParity_ColorPurple, "#6a1b9a")
    ];

    /// <summary>The Windows editor highlight colours; empty clears the highlight.</summary>
    private static readonly (Func<string> Name, string Hex)[] HighlightColors =
    [
        (() => Translator.ComposerParity_ColorNone, string.Empty), (() => Translator.ComposerParity_ColorYellow, "#fff59d"),
        (() => Translator.ComposerParity_ColorGreen, "#c8e6c9"), (() => Translator.ComposerParity_ColorBlue, "#bbdefb"),
        (() => Translator.ComposerParity_ColorPink, "#f8bbd0"), (() => Translator.ComposerParity_ColorOrange, "#ffe0b2")
    ];

    private static readonly (Func<string> Name, string Tag)[] ParagraphStyles =
    [
        (() => Translator.ComposerParity_ParagraphNormal, "p"), (() => Translator.ComposerParity_Heading1, "h1"),
        (() => Translator.ComposerParity_Heading2, "h2"), (() => Translator.ComposerParity_Heading3, "h3"),
        (() => Translator.ComposerParity_Quote, "blockquote"), (() => Translator.ComposerParity_Preformatted, "pre"),
        (() => Translator.ComposerParity_Code, "code")
    ];

    private readonly EditorToolbarLayout _layout;
    private readonly EditorToolbarFeatures _features;
    private readonly IPreferencesService? _preferences;
    private readonly ITranslationService? _translations;
    private readonly Action<Exception>? _error;
    private readonly Dictionary<string, EditorFormatButton> _toggles = [];
    private readonly NSPopUpButton _fontPopup;
    private readonly NSPopUpButton _sizePopup;
    private readonly NSPopUpButton? _paragraphPopup;
    private readonly EditorFormatButton? _removeLink;
    private readonly EditorFormatButton? _imageProperties;
    private readonly EditorFormatButton? _spellCheck;
    private readonly NSSegmentedControl? _tabs;
    private readonly EditorOverflowRow[] _rows;
    private readonly List<EditorToolbarItem> _formatItems = [];
    private readonly List<EditorToolbarItem> _insertItems = [];
    private readonly List<EditorToolbarItem> _insertHostItems = [];
    private readonly List<EditorToolbarItem> _optionsHostItems = [];
    private readonly List<EditorToolbarItem> _spellItems = [];
    private readonly List<EditorToolbarItem> _trailingItems = [];
    private AppKitHtmlMailEditorSession? _session;
    private EventHandler<EditorState>? _stateHandler;
    private EventHandler<EditorShortcutKind>? _shortcutHandler;
    private EventHandler? _imageHandler;
    private ColorPanelTarget? _colorTarget;
    /// <summary>The target the shared colour panel currently sends to (AppKit has no getter for it).</summary>
    private static ColorPanelTarget? s_colorPanelOwner;
    private EditorState _state = new();
    private string _spellLanguage;
    private bool _detached;

    public EditorFormatToolbar(EditorToolbarLayout layout, EditorToolbarFeatures features, IPreferencesService? preferences,
        ITranslationService? translations, Action<Exception>? error)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _layout = layout;
        _features = features;
        _preferences = preferences;
        _translations = translations;
        _error = error;
        _spellLanguage = preferences?.ComposerSpellCheckLanguageCode ?? string.Empty;

        // ---- Format ----
        _fontPopup = Popup(Translator.Composer_Font, 118);
        _fontPopup.AddItems(Fonts);
        _fontPopup.Activated += (_, _) => Run(EditorCommand.SetFontFamily(_fontPopup.TitleOfSelectedItem));
        _sizePopup = Popup(Translator.Composer_FontSize, 52);
        _sizePopup.AddItems(FontSizes.Select(static size => size.ToString()).ToArray());
        _sizePopup.Activated += (_, _) => Run(EditorCommand.SetFontSize(FontSizes[Math.Max(0, (int)_sizePopup.IndexOfSelectedItem)]));
        SelectTypography(preferences?.ComposerFont, preferences?.ComposerFontSize);

        _formatItems.Add(new EditorToolbarItem(_fontPopup, () => Submenu(Translator.Composer_Font,
            Fonts.Select(font => Radio(font, IsCurrentFont(font), () => Run(EditorCommand.SetFontFamily(font)))))));
        _formatItems.Add(new EditorToolbarItem(_sizePopup, () => Submenu(Translator.Composer_FontSize,
            FontSizes.Select(size => Radio(size.ToString(), _state.FontSize == size, () => Run(EditorCommand.SetFontSize(size)))))));
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(Toggle("bold", WinoIconGlyph.TextBold, Translator.Composer_Bold, EditorCommand.ToggleBold, "⌘B", static s => s.IsBold));
        _formatItems.Add(Toggle("italic", WinoIconGlyph.TextItalic, Translator.Composer_Italic, EditorCommand.ToggleItalic, "⌘I", static s => s.IsItalic));
        _formatItems.Add(Toggle("underline", WinoIconGlyph.TextUnderline, Translator.Composer_Underline, EditorCommand.ToggleUnderline, "⌘U", static s => s.IsUnderline));
        _formatItems.Add(Toggle("strike", WinoIconGlyph.TextStrikethrough, Translator.Composer_Strikethrough, EditorCommand.ToggleStrikethrough, null, static s => s.IsStrikethrough));
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(ColorItem(WinoIconGlyph.TextColor, Translator.Composer_TextColor, TextColors, false));
        if (features.Highlight) _formatItems.Add(ColorItem(WinoIconGlyph.Highlight, Translator.Composer_HighlightColor, HighlightColors, true));
        if (features.ClearFormatting) _formatItems.Add(Command(WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, () => Run(EditorCommand.ClearFormatting()), "⌘\\"));
        if (features.Paragraph)
        {
            _paragraphPopup = Popup(Translator.Composer_ParagraphStyle, 112);
            _paragraphPopup.AddItems(ParagraphStyles.Select(static style => style.Name()).ToArray());
            _paragraphPopup.Activated += (_, _) => Run(EditorCommand.SetParagraphStyle(ParagraphStyles[Math.Max(0, (int)_paragraphPopup.IndexOfSelectedItem)].Tag));
            _formatItems.Add(EditorToolbarItem.Divider());
            _formatItems.Add(new EditorToolbarItem(_paragraphPopup, () => Submenu(Translator.Composer_ParagraphStyle,
                ParagraphStyles.Select(style => Radio(style.Name(), string.Equals(_state.ParagraphStyle ?? "p", style.Tag, StringComparison.OrdinalIgnoreCase),
                    () => Run(EditorCommand.SetParagraphStyle(style.Tag)))))));
        }
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(Toggle("bullets", WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList, null, static s => s.IsUnorderedList));
        _formatItems.Add(Toggle("numbers", WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList, null, static s => s.IsOrderedList));
        // Outdent stays enabled: the editor never reports CanOutdent (the Windows button is always disabled).
        _formatItems.Add(Command(WinoIconGlyph.TextIndentDecrease, Translator.Composer_Outdent, () => Run(EditorCommand.Outdent())));
        _formatItems.Add(Command(WinoIconGlyph.TextIndentIncrease, Translator.Composer_Indent, () => Run(EditorCommand.Indent())));
        if (features.Alignment)
        {
            _formatItems.Add(EditorToolbarItem.Divider());
            _formatItems.Add(Alignment("left", WinoIconGlyph.TextAlignLeft, Translator.Composer_AlignLeft, EditorTextAlignment.Left));
            _formatItems.Add(Alignment("center", WinoIconGlyph.TextAlignCenter, Translator.Composer_AlignCenter, EditorTextAlignment.Center));
            _formatItems.Add(Alignment("right", WinoIconGlyph.TextAlignRight, Translator.Composer_AlignRight, EditorTextAlignment.Right));
            _formatItems.Add(Alignment("justify", WinoIconGlyph.TextAlignJustify, Translator.Composer_AlignJustify, EditorTextAlignment.Justify));
            _toggles["left"].IsActive = true;
        }
        if (features.LineSpacing) _formatItems.Add(LineSpacingItem());

        // ---- Insert ----
        if (features.Image) _insertItems.Add(Command(WinoIconGlyph.ImageAdd, Translator.Composer_InsertPicture, () => Run(EditorCommand.InsertImage()), null, Translator.Composer_InsertPictureDescription));
        if (features.Emoji) _insertItems.Add(Command(WinoIconGlyph.Emoji, Translator.Emoji, ShowEmojiPalette, null, Translator.Composer_InsertEmojiDescription));
        if (features.Table) _insertItems.Add(Command(WinoIconGlyph.Table, Translator.Composer_Table, () => Observe(InsertTableAsync()), null, Translator.Composer_InsertTable));
        _insertItems.Add(Command(WinoIconGlyph.Link, Translator.Composer_Link, () => Observe(EditLinkAsync()), "⌘K", Translator.Composer_InsertLink));
        if (features.LinkTools)
        {
            var removeLink = Command(WinoIconGlyph.LinkDismiss, Translator.Composer_RemoveLink, () => Run(EditorCommand.RemoveLink()));
            _removeLink = (EditorFormatButton)removeLink.View;
            _removeLink.Enabled = false;
            _insertItems.Add(new EditorToolbarItem(_removeLink, () => Plain(Translator.Composer_RemoveLink, () => Run(EditorCommand.RemoveLink()), CanRemoveLink)));
            var properties = Command(WinoIconGlyph.Edit, Translator.Composer_ImageProperties, () => Observe(EditImagePropertiesAsync()), null, Translator.Composer_ImagePropertiesDescription);
            _imageProperties = (EditorFormatButton)properties.View;
            _imageProperties.Enabled = false;
            _insertItems.Add(new EditorToolbarItem(_imageProperties, () => Plain(Translator.Composer_ImageProperties, () => Observe(EditImagePropertiesAsync()), _state.IsImageSelected)));
        }

        // ---- Spell check ----
        if (features.SpellCheck)
        {
            EditorFormatButton? spell = null;
            spell = new EditorFormatButton(WinoIconGlyph.TextProofingTools, Translator.Composer_SpellCheck, () => spell!.PopUp(BuildSpellMenu()), Translator.Composer_SpellCheckDescription)
            {
                IsActive = preferences?.IsComposerSpellCheckEnabled ?? true
            };
            _spellCheck = spell;
            _spellItems.Add(new EditorToolbarItem(spell, () => Submenu(Translator.Composer_SpellCheck, MenuItems(BuildSpellMenu()))));
        }

        // ---- Rows ----
        if (layout == EditorToolbarLayout.Tabbed)
        {
            _tabs = NSSegmentedControl.FromLabels([Translator.EditorToolbarOption_Format, Translator.EditorToolbarOption_Insert, Translator.EditorToolbarOption_Options],
                NSSegmentSwitchTracking.SelectOne, ShowSelectedTab);
            _tabs.ControlSize = NSControlSize.Small;
            _tabs.SelectedSegment = 0;
            _tabs.TranslatesAutoresizingMaskIntoConstraints = false;
            _tabs.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
            WinoAccessibility.Label(_tabs, Translator.Composer_EditorToolbarLabel);
            _rows = [NewRow(), NewRow(), NewRow()];
            AddSubview(_tabs);
            NSLayoutConstraint.ActivateConstraints(
            [
                _tabs.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                _tabs.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
            ]);
            foreach (var row in _rows)
            {
                AddSubview(row);
                NSLayoutConstraint.ActivateConstraints(
                [
                    row.LeadingAnchor.ConstraintEqualTo(_tabs.TrailingAnchor, 10),
                    row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
                    row.TopAnchor.ConstraintEqualTo(TopAnchor),
                    row.BottomAnchor.ConstraintEqualTo(BottomAnchor)
                ]);
            }
        }
        else
        {
            _rows = [NewRow()];
            AddSubview(_rows[0]);
            NSLayoutConstraint.ActivateConstraints(
            [
                _rows[0].LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                _rows[0].TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
                _rows[0].TopAnchor.ConstraintEqualTo(TopAnchor),
                _rows[0].BottomAnchor.ConstraintEqualTo(BottomAnchor)
            ]);
        }
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ToolbarRole;
        WinoAccessibility.Label(this, Translator.Composer_EditorToolbarLabel);
        Rebuild();
        ShowSelectedTab();
    }

    /// <summary>The editor state the toolbar last applied.</summary>
    public EditorState CurrentState => _state;

    // ---- Session ----

    /// <summary>Drives <paramref name="session"/>: state, the ⌘K link shortcut and picture insertion requests.</summary>
    public void Attach(AppKitHtmlMailEditorSession session)
    {
        Detach();
        _detached = false;
        _session = session;
        _stateHandler = (_, state) => OnMain(() => ApplyState(state));
        _shortcutHandler = (_, kind) => { if (kind == EditorShortcutKind.OpenLinkDialog) OnMain(() => Observe(EditLinkAsync())); };
        _imageHandler = (_, _) => OnMain(() => Observe(PickImagesAsync()));
        session.StateChanged += _stateHandler;
        session.ShortcutRequested += _shortcutHandler;
        session.ImageInsertionRequested += _imageHandler;
        ApplyState(session.CurrentState);
    }

    /// <summary>Stops driving the session and releases the colour panel; safe to call twice.</summary>
    public void Detach()
    {
        _detached = true;
        if (_session is { } session)
        {
            if (_stateHandler is not null) session.StateChanged -= _stateHandler;
            if (_shortcutHandler is not null) session.ShortcutRequested -= _shortcutHandler;
            if (_imageHandler is not null) session.ImageInsertionRequested -= _imageHandler;
        }
        _session = null;
        _stateHandler = null;
        _shortcutHandler = null;
        _imageHandler = null;
        ReleaseColorPanel();
    }

    public void Run(EditorCommand command)
    {
        if (_detached || _session is not { } session) return;
        Observe(session.ExecuteCommandAsync(command));
    }

    // ---- Host items ----

    /// <summary>Host commands at the start of the Insert commands (Windows InsertCustomContent).</summary>
    public void AddInsertItems(params EditorToolbarItem[] items)
    {
        _insertHostItems.AddRange(items);
        Rebuild();
    }

    /// <summary>Host commands before spell check (Windows OptionsCustomContent).</summary>
    public void AddOptionsItems(params EditorToolbarItem[] items)
    {
        _optionsHostItems.AddRange(items);
        Rebuild();
    }

    /// <summary>Host commands at the end: after spell check in one row, at the end of Options when tabbed.</summary>
    public void AddTrailingItem(EditorToolbarItem item)
    {
        _trailingItems.Add(item);
        Rebuild();
    }

    /// <summary>Re-measures the rows after a host item changed width or availability.</summary>
    public void InvalidateOverflow()
    {
        foreach (var row in _rows) row.Invalidate();
    }

    private EditorOverflowRow NewRow() => new(Translator.Composer_AttachmentMoreOptions);

    private void Rebuild()
    {
        if (_layout == EditorToolbarLayout.Tabbed)
        {
            _rows[0].SetItems(_formatItems);
            _rows[1].SetItems(Join(_insertHostItems, _insertItems));
            _rows[2].SetItems(Join(_optionsHostItems, _spellItems, _trailingItems));
        }
        else
        {
            _rows[0].SetItems(Join(_formatItems, _insertHostItems, _insertItems, _optionsHostItems, _spellItems, _trailingItems));
        }
    }

    /// <summary>Concatenates the non-empty groups with a divider between them.</summary>
    private static List<EditorToolbarItem> Join(params List<EditorToolbarItem>[] groups)
    {
        var items = new List<EditorToolbarItem>();
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;
            if (items.Count > 0) items.Add(EditorToolbarItem.Divider());
            items.AddRange(group);
        }
        return items;
    }

    private void ShowSelectedTab()
    {
        if (_tabs is null) return;
        var index = (int)_tabs.SelectedSegment;
        for (int row = 0; row < _rows.Length; row++) _rows[row].Hidden = row != index;
        _rows[Math.Clamp(index, 0, _rows.Length - 1)].Invalidate();
    }

    // ---- State ----

    private void ApplyState(EditorState state)
    {
        if (_detached) return;
        _state = state;
        SetActive("bold", state.IsBold);
        SetActive("italic", state.IsItalic);
        SetActive("underline", state.IsUnderline);
        SetActive("strike", state.IsStrikethrough);
        SetActive("bullets", state.IsUnorderedList);
        SetActive("numbers", state.IsOrderedList);
        SetActive("left", state.Alignment == EditorTextAlignment.Left);
        SetActive("center", state.Alignment == EditorTextAlignment.Center);
        SetActive("right", state.Alignment == EditorTextAlignment.Right);
        SetActive("justify", state.Alignment == EditorTextAlignment.Justify);
        SelectTypography(state.FontFamily, state.FontSize);
        if (_paragraphPopup is not null)
        {
            var index = Array.FindIndex(ParagraphStyles, style => string.Equals(style.Tag, state.ParagraphStyle ?? "p", StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _paragraphPopup.SelectItem(index);
        }
        if (_removeLink is not null) _removeLink.Enabled = CanRemoveLink;
        if (_imageProperties is not null) _imageProperties.Enabled = state.IsImageSelected;
        if (_spellCheck is not null) _spellCheck.IsActive = state.IsSpellCheckEnabled;
    }

    private bool CanRemoveLink => !string.IsNullOrWhiteSpace(_state.LinkUrl) || (_state.IsImageSelected && !string.IsNullOrWhiteSpace(_state.ImageLinkUrl));

    private void SetActive(string key, bool active)
    {
        if (_toggles.TryGetValue(key, out var button)) button.IsActive = active;
    }

    private void SelectTypography(string? fontFamily, int? size)
    {
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            int index = Array.FindIndex(Fonts, font => string.Equals(font, NormalizeFamily(fontFamily), StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _fontPopup.SelectItem(index);
        }
        if (size is { } points)
        {
            int index = Array.IndexOf(FontSizes, points);
            if (index >= 0) _sizePopup.SelectItem(index);
        }
    }

    private static string NormalizeFamily(string family) => family.Split(',')[0].Trim().Trim('"', '\'');

    private bool IsCurrentFont(string font)
        => !string.IsNullOrWhiteSpace(_state.FontFamily) && string.Equals(NormalizeFamily(_state.FontFamily), font, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The editor reports the computed line height in pixels; the preset is the one whose ratio to the
    /// font size is within 0.05. "normal" matches the Default preset.
    /// </summary>
    internal static string? MatchLineHeight(string? computed, int? fontSize)
    {
        if (string.IsNullOrWhiteSpace(computed)) return null;
        var value = computed.Trim();
        if (string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase)) return "normal";
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            if (fontSize is not > 0 || !double.TryParse(value[..^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixels)) return null;
            var ratio = pixels / fontSize.Value;
            foreach (var preset in LineHeights.Skip(1))
                if (Math.Abs(double.Parse(preset, System.Globalization.CultureInfo.InvariantCulture) - ratio) <= 0.05) return preset;
            return null;
        }
        return LineHeights.FirstOrDefault(preset => string.Equals(preset, value, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Items ----

    private EditorToolbarItem Toggle(string key, WinoIconGlyph glyph, string label, Func<EditorCommand> command, string? shortcut, Func<EditorState, bool> isOn)
    {
        var button = new EditorFormatButton(glyph, label, () => Run(command()), Tooltip(label, shortcut));
        _toggles[key] = button;
        return new EditorToolbarItem(button, () => Check(label, isOn(_state), () => Run(command())));
    }

    private EditorToolbarItem Alignment(string key, WinoIconGlyph glyph, string label, EditorTextAlignment alignment)
    {
        // A choice, not a switch: clicking the active alignment keeps it.
        var button = new EditorFormatButton(glyph, label, () => { SetAlignmentActive(alignment); Run(EditorCommand.SetAlignment(alignment)); });
        _toggles[key] = button;
        return new EditorToolbarItem(button, () => Radio(label, _state.Alignment == alignment, () => Run(EditorCommand.SetAlignment(alignment))));
    }

    private void SetAlignmentActive(EditorTextAlignment alignment)
    {
        SetActive("left", alignment == EditorTextAlignment.Left);
        SetActive("center", alignment == EditorTextAlignment.Center);
        SetActive("right", alignment == EditorTextAlignment.Right);
        SetActive("justify", alignment == EditorTextAlignment.Justify);
    }

    private static EditorToolbarItem Command(WinoIconGlyph glyph, string label, Action action, string? shortcut = null, string? tooltip = null)
    {
        var button = new EditorFormatButton(glyph, label, action, Tooltip(tooltip ?? label, shortcut));
        return new EditorToolbarItem(button, () => Plain(label, action));
    }

    private EditorToolbarItem ColorItem(WinoIconGlyph glyph, string label, (Func<string> Name, string Hex)[] colors, bool highlight)
    {
        EditorFormatButton? button = null;
        button = new EditorFormatButton(glyph, label, () => button!.PopUp(BuildColorMenu(colors, highlight)));
        return new EditorToolbarItem(button, () => Submenu(label, MenuItems(BuildColorMenu(colors, highlight))));
    }

    private EditorToolbarItem LineSpacingItem()
    {
        EditorFormatButton? button = null;
        button = new EditorFormatButton(WinoIconGlyph.TextLineSpacing, Translator.Composer_LineSpacing, () => button!.PopUp(BuildLineSpacingMenu()));
        return new EditorToolbarItem(button, () => Submenu(Translator.Composer_LineSpacing, MenuItems(BuildLineSpacingMenu())));
    }

    private static string Tooltip(string label, string? shortcut) => shortcut is null ? label : $"{label} ({shortcut})";

    private static NSPopUpButton Popup(string label, double width)
    {
        var popup = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        popup.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize);
        popup.WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
        popup.ToolTip = label;
        WinoAccessibility.Label(popup, label);
        return popup;
    }

    // ---- Menus (built on demand, discarded on close) ----

    private NSMenu BuildColorMenu((Func<string> Name, string Hex)[] colors, bool highlight)
    {
        var current = highlight ? _state.HighlightColor : _state.TextColor;
        var menu = new NSMenu { AutoEnablesItems = false };
        foreach (var (name, hex) in colors)
        {
            var value = hex;
            var item = new NSMenuItem(name(), (_, _) => Run(highlight ? EditorCommand.SetHighlightColor(value) : EditorCommand.SetTextColor(value)))
            {
                Image = Swatch(string.IsNullOrEmpty(hex) ? null : WinoStyle.FromHexString(hex)),
                State = SameColor(current, hex) ? NSCellStateValue.On : NSCellStateValue.Off
            };
            menu.AddItem(item);
        }
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.ComposerParity_MoreColors, (_, _) => ShowColorPanel(highlight)));
        return menu;
    }

    private static bool SameColor(string? current, string hex)
    {
        if (string.IsNullOrWhiteSpace(current) || current is "transparent" or "rgba(0, 0, 0, 0)") return string.IsNullOrEmpty(hex);
        if (string.IsNullOrEmpty(hex)) return false;
        return string.Equals(NormalizeColor(current), hex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"rgb(198, 40, 40)" or "#C62828" as "#c62828".</summary>
    internal static string? NormalizeColor(string value)
    {
        value = value.Trim();
        if (value.StartsWith('#')) return value.ToLowerInvariant();
        if (!value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return null;
        var start = value.IndexOf('(');
        var end = value.IndexOf(')');
        if (start < 0 || end <= start) return null;
        var parts = value[(start + 1)..end].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !int.TryParse(parts[0], out var r) || !int.TryParse(parts[1], out var g) || !int.TryParse(parts[2], out var b)) return null;
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    private static NSImage Swatch(NSColor? color) => NSImage.ImageWithSize(new CGSize(12, 12), false, rect =>
    {
        var path = NSBezierPath.FromRoundedRect(rect.Inset(1, 1), 2, 2);
        if (color is not null)
        {
            color.SetFill();
            path.Fill();
        }
        else
        {
            NSColor.SecondaryLabel.SetStroke();
            path.LineWidth = 1;
            path.Stroke();
        }
        return true;
    });

    private NSMenu BuildLineSpacingMenu()
    {
        var current = MatchLineHeight(_state.LineHeight, _state.FontSize);
        var menu = new NSMenu { AutoEnablesItems = false };
        foreach (var preset in LineHeights)
        {
            var value = preset;
            var title = value == "normal" ? Translator.Composer_LineSpacingDefault : value;
            menu.AddItem(Radio(title, current == value, () => Run(EditorCommand.SetLineHeight(value))));
        }
        return menu;
    }

    private NSMenu BuildSpellMenu()
    {
        var enabled = _state.IsSpellCheckEnabled;
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(Check(Translator.Composer_SpellCheck, enabled, () => SetSpellCheck(!enabled)));
        if (_translations is null) return menu;

        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.SettingsComposerSpellCheckLanguage_Title) { Enabled = false });
        menu.AddItem(Radio(Translator.ComposerParity_SpellCheckAutomatic, string.IsNullOrEmpty(_spellLanguage), () => SetSpellCheckLanguage(string.Empty)));
        foreach (var language in _translations.GetAvailableLanguages())
        {
            var code = language.Code;
            menu.AddItem(Radio(language.DisplayName, string.Equals(code, _spellLanguage, StringComparison.OrdinalIgnoreCase), () => SetSpellCheckLanguage(code)));
        }
        return menu;
    }

    /// <summary>Turns spell check on or off and keeps it as the composer preference, like Windows.</summary>
    private void SetSpellCheck(bool enabled)
    {
        if (_preferences is not null) _preferences.IsComposerSpellCheckEnabled = enabled;
        if (_spellCheck is not null) _spellCheck.IsActive = enabled;
        Run(EditorCommand.ToggleSpellCheck(enabled));
    }

    private void SetSpellCheckLanguage(string code)
    {
        if (string.Equals(code, _spellLanguage, StringComparison.OrdinalIgnoreCase)) return;
        _spellLanguage = code;
        if (_preferences is not null) _preferences.ComposerSpellCheckLanguageCode = code;
        Run(EditorCommand.SetSpellCheckLanguage(code));
    }

    private static NSMenuItem Plain(string title, Action action, bool enabled = true)
        => new(title, (_, _) => action()) { Enabled = enabled };

    private static NSMenuItem Check(string title, bool on, Action action)
        => new(title, (_, _) => action()) { State = on ? NSCellStateValue.On : NSCellStateValue.Off };

    private static NSMenuItem Radio(string title, bool on, Action action) => Check(title, on, action);

    private static NSMenuItem Submenu(string title, IEnumerable<NSMenuItem> items)
    {
        var menu = new NSMenu(title) { AutoEnablesItems = false };
        foreach (var item in items) menu.AddItem(item);
        return new NSMenuItem(title) { Submenu = menu };
    }

    /// <summary>Moves the items out of <paramref name="menu"/> so they can join a submenu.</summary>
    private static IEnumerable<NSMenuItem> MenuItems(NSMenu menu)
    {
        var items = menu.Items;
        menu.RemoveAllItems();
        return items;
    }

    // ---- Colour panel ----

    private void ShowColorPanel(bool highlight)
    {
        var panel = NSColorPanel.SharedColorPanel;
        _colorTarget ??= new ColorPanelTarget(this);
        _colorTarget.Highlight = highlight;
        panel.ShowsAlpha = false;
        panel.Continuous = false;
        panel.SetTarget(_colorTarget);
        s_colorPanelOwner = _colorTarget;
        panel.SetAction(new Selector("colorChanged:"));
        panel.OrderFront(null);
    }

    private void ApplyPanelColor(NSColor color, bool highlight)
    {
        if (color.UsingColorSpace(NSColorSpace.SRGBColorSpace) is not { } rgb) return;
        var hex = $"#{(int)Math.Round(rgb.RedComponent * 255):x2}{(int)Math.Round(rgb.GreenComponent * 255):x2}{(int)Math.Round(rgb.BlueComponent * 255):x2}";
        Run(highlight ? EditorCommand.SetHighlightColor(hex) : EditorCommand.SetTextColor(hex));
    }

    private void ReleaseColorPanel()
    {
        if (_colorTarget is null) return;
        var panel = NSColorPanel.SharedColorPanel;
        // Another editor (a signature sheet over the composer) may own the panel now.
        if (ReferenceEquals(s_colorPanelOwner, _colorTarget))
        {
            panel.SetTarget(null!);
            panel.SetAction(null!);
            if (panel.IsVisible) panel.OrderOut(null);
            s_colorPanelOwner = null;
        }
        _colorTarget.Dispose();
        _colorTarget = null;
    }

    private sealed class ColorPanelTarget(EditorFormatToolbar owner) : NSObject
    {
        public bool Highlight { get; set; }

        [Export("colorChanged:")]
        public void ColorChanged(NSObject sender)
        {
            if (sender is NSColorPanel panel) owner.ApplyPanelColor(panel.Color, Highlight);
        }
    }

    // ---- Insert commands ----

    /// <summary>The macOS Character Viewer inserts the chosen emoji at the editor caret.</summary>
    private void ShowEmojiPalette()
    {
        if (_detached || _session is not { } session) return;
        Observe(ShowEmojiPaletteAsync(session));
    }

    private async Task ShowEmojiPaletteAsync(AppKitHtmlMailEditorSession session)
    {
        await session.FocusEditorAsync(true);
        OnMain(() => { if (!_detached) NSApplication.SharedApplication.OrderFrontCharacterPalette(this); });
    }

    public async Task EditLinkAsync()
    {
        if (_detached || _session is not { } session || Window is not { } window) return;
        var args = await EditorDialogs.LinkAsync(window, session.CurrentState);
        if (args is not null) Run(EditorCommand.InsertLink(args));
    }

    public async Task EditImagePropertiesAsync()
    {
        if (_detached || _session is not { } session || Window is not { } window) return;
        var args = await EditorDialogs.ImagePropertiesAsync(window, session.CurrentState);
        if (args is not null) Run(EditorCommand.SetImageProperties(args));
    }

    public async Task InsertTableAsync()
    {
        if (_detached || Window is not { } window) return;
        var args = await EditorDialogs.TableAsync(window);
        if (args is not null) Run(EditorCommand.InsertTable(args));
    }

    private async Task PickImagesAsync()
    {
        if (_detached) return;
        var images = await EditorDialogs.PickImagesAsync();
        if (images.Count > 0 && !_detached && _session is { } session) await session.InsertImagesAsync(images);
    }

    // ---- Debug ----

    public void SelectTab(int index)
    {
        if (_tabs is null) return;
        _tabs.SelectedSegment = Math.Clamp(index, 0, 2);
        ShowSelectedTab();
    }

    public void ShowOverflowMenu() => VisibleRow.ShowOverflowMenu();

    private EditorOverflowRow VisibleRow => _rows.FirstOrDefault(static row => !row.Hidden) ?? _rows[0];

    public string Describe()
    {
        var row = VisibleRow;
        string Name(EditorToolbarItem item) => item.IsDivider ? "|" : item.View is EditorFormatButton button ? button.Label : item.View.AccessibilityLabel ?? item.View.GetType().Name;
        var visible = row.Items.Where(static item => !item.View.Hidden).Select(Name);
        var overflow = row.OverflowItems.Where(static item => !item.IsDivider).Select(Name);
        return $"width={row.Bounds.Width:0} more={row.IsOverflowing} visible=[{string.Join(", ", visible)}] overflow=[{string.Join(", ", overflow)}]";
    }

    // ---- Helpers ----

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _error?.Invoke(exception); }
    }

    private static void OnMain(Action action) => NSApplication.SharedApplication.BeginInvokeOnMainThread(action);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Detach();
            _formatItems.Clear();
            _insertItems.Clear();
            _insertHostItems.Clear();
            _optionsHostItems.Clear();
            _spellItems.Clear();
            _trailingItems.Clear();
            _toggles.Clear();
        }
        base.Dispose(disposing);
    }
}
