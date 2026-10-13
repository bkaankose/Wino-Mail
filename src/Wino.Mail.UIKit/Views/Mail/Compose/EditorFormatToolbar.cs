using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Common;
using Wino.Editor;
using Wino.Editor.WebKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// The composer's formatting bar above the keyboard (Mac EditorFormatToolbar, Windows EditorTabbedCommandBarControl):
/// a Format / Insert / Options section switch, the section's commands in a horizontally scrolling row, and Hide
/// keyboard. Format has font and size, B/I/U/S, colours, clear formatting, paragraph style, lists, indent, alignment
/// and line spacing; Insert has the host's attach command, picture, table, link, remove link and image properties;
/// Options has the host's commands and spell check with its language. Menus are UIMenus built when they open, so
/// they show the current editor state. Main thread only; call <see cref="Detach"/> before disposing the session.
/// </summary>
internal sealed class EditorFormatToolbar : UIInputView
{
    internal static readonly string[] Fonts = ["Helvetica Neue", "Helvetica", "Arial", "Times New Roman", "Georgia", "Verdana", "Courier New"];
    internal static readonly int[] FontSizes = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 48];
    internal static readonly string[] LineHeights = ["normal", "1", "1.15", "1.5", "2"];
    private const double BarHeight = 46;

    /// <summary>The Windows editor text colours; empty means automatic.</summary>
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

    private static readonly (EditorTextAlignment Value, WinoIconGlyph Glyph, Func<string> Name)[] Alignments =
    [
        (EditorTextAlignment.Left, WinoIconGlyph.TextAlignLeft, () => Translator.Composer_AlignLeft),
        (EditorTextAlignment.Center, WinoIconGlyph.TextAlignCenter, () => Translator.Composer_AlignCenter),
        (EditorTextAlignment.Right, WinoIconGlyph.TextAlignRight, () => Translator.Composer_AlignRight),
        (EditorTextAlignment.Justify, WinoIconGlyph.TextAlignJustify, () => Translator.Composer_AlignJustify)
    ];

    private readonly IPreferencesService? _preferences;
    private readonly ITranslationService? _translations;
    private readonly Action<Exception>? _error;
    private readonly Func<UIViewController?> _presenter;
    private readonly Func<Task<List<SharedFile>>> _pickImages;
    private readonly Dictionary<string, EditorToolbarButton> _toggles = [];
    private readonly List<EditorToolbarItem> _formatItems = [];
    private readonly List<EditorToolbarItem> _insertItems = [];
    private readonly List<EditorToolbarItem> _insertHostItems = [];
    private readonly List<EditorToolbarItem> _optionsHostItems = [];
    private readonly List<EditorToolbarItem> _spellItems = [];
    private readonly UISegmentedControl _sections;
    private readonly UIScrollView _scroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, ShowsHorizontalScrollIndicator = false, AlwaysBounceHorizontal = true };
    private readonly UIStackView _row = WinoLayout.HStack(2);
    private readonly EditorToolbarButton _alignment;
    private readonly EditorToolbarButton _removeLink;
    private readonly EditorToolbarButton _imageProperties;
    private readonly EditorToolbarButton _spellCheck;
    private WebKitHtmlMailEditorSession? _session;
    private EventHandler<EditorState>? _stateHandler;
    private EventHandler<EditorShortcutKind>? _shortcutHandler;
    private EventHandler? _imageHandler;
    private EditorState _state = new();
    private string _spellLanguage;
    private bool _detached = true;

    public EditorFormatToolbar(IPreferencesService? preferences, ITranslationService? translations, Action<Exception>? error,
        Func<UIViewController?> presenter, Func<Task<List<SharedFile>>> pickImages, Action hideKeyboard)
        : base(new CGRect(0, 0, 320, BarHeight), UIInputViewStyle.Keyboard)
    {
        _preferences = preferences;
        _translations = translations;
        _error = error;
        _presenter = presenter;
        _pickImages = pickImages;
        _spellLanguage = preferences?.ComposerSpellCheckLanguageCode ?? string.Empty;
        AutoresizingMask = UIViewAutoresizing.FlexibleWidth;
        AllowsSelfSizing = false;

        // ---- Format ----
        _formatItems.Add(Menu(WinoIconGlyph.TextFont, Translator.Composer_Font, BuildFontMenu));
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(Toggle("bold", WinoIconGlyph.TextBold, Translator.Composer_Bold, EditorCommand.ToggleBold));
        _formatItems.Add(Toggle("italic", WinoIconGlyph.TextItalic, Translator.Composer_Italic, EditorCommand.ToggleItalic));
        _formatItems.Add(Toggle("underline", WinoIconGlyph.TextUnderline, Translator.Composer_Underline, EditorCommand.ToggleUnderline));
        _formatItems.Add(Toggle("strike", WinoIconGlyph.TextStrikethrough, Translator.Composer_Strikethrough, EditorCommand.ToggleStrikethrough));
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(Menu(WinoIconGlyph.TextColor, Translator.Composer_TextColor, () => BuildColorMenu(TextColors, false)));
        _formatItems.Add(Menu(WinoIconGlyph.Highlight, Translator.Composer_HighlightColor, () => BuildColorMenu(HighlightColors, true)));
        _formatItems.Add(Command(WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, () => Run(EditorCommand.ClearFormatting())));
        _formatItems.Add(Menu(WinoIconGlyph.TextDescription, Translator.Composer_ParagraphStyle, BuildParagraphMenu));
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(Toggle("bullets", WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList));
        _formatItems.Add(Toggle("numbers", WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList));
        // Outdent stays enabled: the editor never reports CanOutdent (the Windows button is always disabled).
        _formatItems.Add(Command(WinoIconGlyph.TextIndentDecrease, Translator.Composer_Outdent, () => Run(EditorCommand.Outdent())));
        _formatItems.Add(Command(WinoIconGlyph.TextIndentIncrease, Translator.Composer_Indent, () => Run(EditorCommand.Indent())));
        _alignment = new EditorToolbarButton(WinoIconGlyph.TextAlignLeft, Translator.Composer_AlignLeft, null, BuildAlignmentMenu);
        _formatItems.Add(new EditorToolbarItem(_alignment));
        _formatItems.Add(Menu(WinoIconGlyph.TextLineSpacing, Translator.Composer_LineSpacing, BuildLineSpacingMenu));

        // ---- Insert ----
        _insertItems.Add(Command(WinoIconGlyph.ImageAdd, Translator.Composer_InsertPicture, () => Observe(PickImagesAsync())));
        _insertItems.Add(Command(WinoIconGlyph.Table, Translator.Composer_InsertTable, () => Observe(InsertTableAsync())));
        _insertItems.Add(Command(WinoIconGlyph.Link, Translator.Composer_InsertLink, () => Observe(EditLinkAsync())));
        _removeLink = new EditorToolbarButton(WinoIconGlyph.LinkDismiss, Translator.Composer_RemoveLink, () => Run(EditorCommand.RemoveLink())) { Enabled = false };
        _insertItems.Add(new EditorToolbarItem(_removeLink));
        _imageProperties = new EditorToolbarButton(WinoIconGlyph.Edit, Translator.Composer_ImageProperties, () => Observe(EditImagePropertiesAsync())) { Enabled = false };
        _insertItems.Add(new EditorToolbarItem(_imageProperties));

        // ---- Spell check (end of Options) ----
        _spellCheck = new EditorToolbarButton(WinoIconGlyph.TextProofingTools, Translator.Composer_SpellCheck, null, BuildSpellMenu)
        {
            IsActive = preferences?.IsComposerSpellCheckEnabled ?? true
        };
        _spellItems.Add(new EditorToolbarItem(_spellCheck));

        // ---- Bar ----
        _sections = new UISegmentedControl(
        [
            WinoIcons.Image(WinoIconGlyph.TextFont, 16, null, Translator.EditorToolbarOption_Format),
            WinoIcons.Image(WinoIconGlyph.Add, 16, null, Translator.EditorToolbarOption_Insert),
            WinoIcons.Image(WinoIconGlyph.Settings, 16, null, Translator.EditorToolbarOption_Options)
        ])
        { SelectedSegment = 0, TranslatesAutoresizingMaskIntoConstraints = false };
        _sections.AccessibilityLabel = Translator.Composer_EditorToolbarLabel;
        _sections.ValueChanged += (_, _) => Rebuild();
        _sections.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);

        _row.Alignment = UIStackViewAlignment.Center;
        _scroll.AddSubview(_row);
        _row.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            _row.LeadingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.LeadingAnchor, 4),
            _row.TrailingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TrailingAnchor, -4),
            _row.TopAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TopAnchor),
            _row.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor),
            _row.HeightAnchor.ConstraintEqualTo(_scroll.FrameLayoutGuide.HeightAnchor)
        ]);

        var hide = new EditorToolbarButton(WinoIconGlyph.Keyboard, Translator.Composer_HideKeyboard, hideKeyboard);
        var bar = WinoLayout.HStack(6, _sections, new WinoSeparator(true), _scroll, new WinoSeparator(true), hide);
        WinoLayout.Insets(bar, 4, 8, 4, 4);
        _scroll.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _scroll.SetContentCompressionResistancePriority(1, UILayoutConstraintAxis.Horizontal);
        _scroll.HeightAnchor.ConstraintEqualTo(38).Active = true;
        WinoLayout.Fill(bar, this);

        IsAccessibilityElement = false;
        AccessibilityLabel = Translator.Composer_EditorToolbarLabel;
        AccessibilityTraits = UIAccessibilityTrait.None;
        Rebuild();
    }

    public override CGSize IntrinsicContentSize => new(NoIntrinsicMetric, BarHeight);

    /// <summary>The editor state the toolbar last applied.</summary>
    public EditorState CurrentState => _state;

    /// <summary>The selected section: 0 Format, 1 Insert, 2 Options.</summary>
    public int SelectedSection => (int)_sections.SelectedSegment;

    // ---- Session ----

    /// <summary>Drives <paramref name="session"/>: state, the link shortcut and picture insertion requests.</summary>
    public void Attach(WebKitHtmlMailEditorSession session)
    {
        Detach();
        _detached = false;
        _session = session;
        _stateHandler = (_, state) => BeginInvokeOnMainThread(() => ApplyState(state));
        _shortcutHandler = (_, kind) => { if (kind == EditorShortcutKind.OpenLinkDialog) BeginInvokeOnMainThread(() => Observe(EditLinkAsync())); };
        _imageHandler = (_, _) => BeginInvokeOnMainThread(() => Observe(PickImagesAsync()));
        session.StateChanged += _stateHandler;
        session.ShortcutRequested += _shortcutHandler;
        session.ImageInsertionRequested += _imageHandler;
        ApplyState(session.CurrentState);
    }

    /// <summary>Stops driving the session; safe to call twice.</summary>
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
    }

    public void Run(EditorCommand command)
    {
        if (_detached || _session is not { } session) return;
        Observe(session.ExecuteCommandAsync(command));
    }

    // ---- Host items ----

    /// <summary>Host commands at the start of Insert (Windows InsertCustomContent).</summary>
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

    /// <summary>Re-applies host item availability (S/MIME without a certificate, for example).</summary>
    public void InvalidateItems() => Rebuild();

    public void SelectSection(int index)
    {
        _sections.SelectedSegment = Math.Clamp(index, 0, 2);
        Rebuild();
    }

    private void Rebuild()
    {
        var items = SelectedSection switch
        {
            1 => Join(_insertHostItems, _insertItems),
            2 => Join(_optionsHostItems, _spellItems),
            _ => _formatItems
        };
        foreach (var view in _row.ArrangedSubviews)
        {
            _row.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
        EditorToolbarItem? previous = null;
        foreach (var item in items)
        {
            if (!item.IsDivider && !item.IsAvailable) continue;
            // No divider at the start or twice in a row.
            if (item.IsDivider && (previous is null || previous.IsDivider)) continue;
            _row.AddArrangedSubview(item.View);
            previous = item;
        }
        if (previous is { IsDivider: true }) { _row.RemoveArrangedSubview(previous.View); previous.View.RemoveFromSuperview(); }
        _scroll.ContentOffset = CGPoint.Empty;
    }

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

    public string Describe()
    {
        var names = _row.ArrangedSubviews.Select(static view => view is EditorToolbarButton button ? button.Label : "|");
        return $"section={SelectedSection} items=[{string.Join(", ", names)}]";
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
        var alignment = Alignments.FirstOrDefault(item => item.Value == state.Alignment);
        if (alignment.Name is not null) _alignment.SetGlyph(alignment.Glyph, alignment.Name());
        _removeLink.Enabled = CanRemoveLink;
        _imageProperties.Enabled = state.IsImageSelected;
        _spellCheck.IsActive = state.IsSpellCheckEnabled;
    }

    private bool CanRemoveLink => !string.IsNullOrWhiteSpace(_state.LinkUrl) || (_state.IsImageSelected && !string.IsNullOrWhiteSpace(_state.ImageLinkUrl));

    private void SetActive(string key, bool active)
    {
        if (_toggles.TryGetValue(key, out var button)) button.IsActive = active;
    }

    private static string NormalizeFamily(string family) => family.Split(',')[0].Trim().Trim('"', '\'');

    private bool IsCurrentFont(string font)
    {
        var family = string.IsNullOrWhiteSpace(_state.FontFamily) ? _preferences?.ComposerFont : _state.FontFamily;
        return !string.IsNullOrWhiteSpace(family) && string.Equals(NormalizeFamily(family), font, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The preset whose ratio to the font size is within 0.05 of the computed line height; "normal" is the default.</summary>
    internal static string? MatchLineHeight(string? computed, int? fontSize)
    {
        if (string.IsNullOrWhiteSpace(computed)) return null;
        var value = computed.Trim();
        if (string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase)) return "normal";
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            if (fontSize is not { } size || size <= 0 || !double.TryParse(value[..^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixels)) return null;
            var ratio = pixels / size;
            foreach (var preset in LineHeights.Skip(1))
                if (Math.Abs(double.Parse(preset, System.Globalization.CultureInfo.InvariantCulture) - ratio) <= 0.05) return preset;
            return null;
        }
        return LineHeights.FirstOrDefault(preset => string.Equals(preset, value, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Items ----

    private EditorToolbarItem Toggle(string key, WinoIconGlyph glyph, string label, Func<EditorCommand> command)
    {
        var button = new EditorToolbarButton(glyph, label, () => Run(command()));
        _toggles[key] = button;
        return new EditorToolbarItem(button);
    }

    private static EditorToolbarItem Command(WinoIconGlyph glyph, string label, Action action)
        => new(new EditorToolbarButton(glyph, label, action));

    private static EditorToolbarItem Menu(WinoIconGlyph glyph, string label, Func<UIMenuElement[]> menu)
        => new(new EditorToolbarButton(glyph, label, null, menu));

    // ---- Menus (built when they open) ----

    internal static UIAction Action(string title, Action action, bool on = false, bool enabled = true, UIImage? image = null, bool destructive = false)
    {
        var item = UIAction.Create(title, image, null, _ => action());
        item.State = on ? UIMenuElementState.On : UIMenuElementState.Off;
        var attributes = default(UIMenuElementAttributes);
        if (!enabled) attributes |= UIMenuElementAttributes.Disabled;
        if (destructive) attributes |= UIMenuElementAttributes.Destructive;
        item.Attributes = attributes;
        return item;
    }

    internal static UIMenu Submenu(string title, IEnumerable<UIMenuElement> children, UIImage? image = null)
        => UIMenu.Create(title, image, UIMenuIdentifier.None, default(UIMenuOptions), children.ToArray());

    internal static UIMenu Inline(IEnumerable<UIMenuElement> children, string title = "")
        => UIMenu.Create(title, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, children.ToArray());

    private UIMenuElement[] BuildFontMenu()
    {
        var size = _state.FontSize ?? _preferences?.ComposerFontSize;
        return
        [
            Inline(Fonts.Select(font => (UIMenuElement)Action(font, () => Run(EditorCommand.SetFontFamily(font)), IsCurrentFont(font))), Translator.Composer_Font),
            Submenu(Translator.Composer_FontSize, FontSizes.Select(points => (UIMenuElement)Action(points.ToString(), () => Run(EditorCommand.SetFontSize(points)), size == points)))
        ];
    }

    private UIMenuElement[] BuildColorMenu((Func<string> Name, string Hex)[] colors, bool highlight)
    {
        var current = highlight ? _state.HighlightColor : _state.TextColor;
        return colors.Select(color =>
        {
            var hex = color.Hex;
            return (UIMenuElement)Action(color.Name(), () => Run(highlight ? EditorCommand.SetHighlightColor(hex) : EditorCommand.SetTextColor(hex)),
                SameColor(current, hex), image: Swatch(string.IsNullOrEmpty(hex) ? null : WinoStyle.FromHexString(hex)));
        }).ToArray();
    }

    private UIMenuElement[] BuildParagraphMenu()
        => ParagraphStyles.Select(style => (UIMenuElement)Action(style.Name(), () => Run(EditorCommand.SetParagraphStyle(style.Tag)),
            string.Equals(_state.ParagraphStyle ?? "p", style.Tag, StringComparison.OrdinalIgnoreCase))).ToArray();

    private UIMenuElement[] BuildAlignmentMenu()
        => Alignments.Select(alignment => (UIMenuElement)Action(alignment.Name(), () =>
        {
            _alignment.SetGlyph(alignment.Glyph, alignment.Name());
            Run(EditorCommand.SetAlignment(alignment.Value));
        }, _state.Alignment == alignment.Value, image: WinoIcons.Image(alignment.Glyph, 16))).ToArray();

    private UIMenuElement[] BuildLineSpacingMenu()
    {
        var current = MatchLineHeight(_state.LineHeight, _state.FontSize);
        return LineHeights.Select(preset => (UIMenuElement)Action(preset == "normal" ? Translator.Composer_LineSpacingDefault : preset,
            () => Run(EditorCommand.SetLineHeight(preset)), current == preset)).ToArray();
    }

    private UIMenuElement[] BuildSpellMenu()
    {
        var enabled = _state.IsSpellCheckEnabled;
        var items = new List<UIMenuElement> { Action(Translator.Composer_SpellCheck, () => SetSpellCheck(!enabled), enabled) };
        if (_translations is null) return items.ToArray();
        var languages = new List<UIMenuElement>
        {
            Action(Translator.ComposerParity_SpellCheckAutomatic, () => SetSpellCheckLanguage(string.Empty), string.IsNullOrEmpty(_spellLanguage))
        };
        foreach (var language in _translations.GetAvailableLanguages())
        {
            var code = language.Code;
            languages.Add(Action(language.DisplayName, () => SetSpellCheckLanguage(code), string.Equals(code, _spellLanguage, StringComparison.OrdinalIgnoreCase)));
        }
        items.Add(Inline(languages, Translator.SettingsComposerSpellCheckLanguage_Title));
        return items.ToArray();
    }

    /// <summary>Turns spell check on or off and keeps it as the composer preference, like Windows.</summary>
    private void SetSpellCheck(bool enabled)
    {
        if (_preferences is not null) _preferences.IsComposerSpellCheckEnabled = enabled;
        _spellCheck.IsActive = enabled;
        Run(EditorCommand.ToggleSpellCheck(enabled));
    }

    private void SetSpellCheckLanguage(string code)
    {
        if (string.Equals(code, _spellLanguage, StringComparison.OrdinalIgnoreCase)) return;
        _spellLanguage = code;
        if (_preferences is not null) _preferences.ComposerSpellCheckLanguageCode = code;
        Run(EditorCommand.SetSpellCheckLanguage(code));
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

    /// <summary>A 14pt colour swatch for a menu row; null draws the outline used for automatic and none.</summary>
    private static UIImage Swatch(UIColor? color)
    {
        using var renderer = new UIGraphicsImageRenderer(new CGSize(14, 14));
        var image = renderer.CreateImage(_ =>
        {
            var path = UIBezierPath.FromRoundedRect(new CGRect(1, 1, 12, 12), 3);
            if (color is not null)
            {
                color.SetFill();
                path.Fill();
            }
            else
            {
                UIColor.SecondaryLabel.SetStroke();
                path.LineWidth = 1;
                path.Stroke();
            }
        });
        return image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
    }

    // ---- Insert commands ----

    public async Task EditLinkAsync()
    {
        if (_detached || _session is not { } session || _presenter() is not { } presenter) return;
        var args = await EditorDialogs.LinkAsync(presenter, session.CurrentState);
        if (args is not null) Run(EditorCommand.InsertLink(args));
    }

    public async Task EditImagePropertiesAsync()
    {
        if (_detached || _session is not { } session || _presenter() is not { } presenter) return;
        var args = await EditorDialogs.ImagePropertiesAsync(presenter, session.CurrentState);
        if (args is not null) Run(EditorCommand.SetImageProperties(args));
    }

    public async Task InsertTableAsync()
    {
        if (_detached || _presenter() is not { } presenter) return;
        var args = await EditorDialogs.TableAsync(presenter);
        if (args is not null) Run(EditorCommand.InsertTable(args));
    }

    private async Task PickImagesAsync()
    {
        if (_detached) return;
        var images = EditorDialogs.ToInlineImages(await _pickImages());
        if (images.Count > 0 && !_detached && _session is { } session) await session.InsertImagesAsync(images);
    }

    // ---- Helpers ----

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _error?.Invoke(exception); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Detach();
            _toggles.Clear();
        }
        base.Dispose(disposing);
    }
}
