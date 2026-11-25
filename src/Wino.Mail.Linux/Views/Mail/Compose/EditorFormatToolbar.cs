using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Editor.WebKitGtk;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal enum EditorToolbarLayout
{
    SingleRow,
    Tabbed
}

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
/// The HTML editor formatting toolbar for every Linux editor surface (Mac <c>EditorFormatToolbar</c>, Windows
/// EditorTabbedCommandBarControl): font, size, B/I/U/S, colours, clear formatting, paragraph style, lists, indent,
/// alignment and line spacing. Insert and Options commands come from their packages through <see cref="AddInsertItems"/>
/// and <see cref="AddOptionsItems"/>. Each row moves what does not fit into its More menu. Main thread only; call
/// <see cref="Detach"/> before the session is disposed.
/// </summary>
internal sealed class EditorFormatToolbar : IDisposable
{
    internal static readonly string[] Fonts = ["Helvetica Neue", "Helvetica", "Arial", "Times New Roman", "Georgia", "Verdana", "Courier New"];
    internal static readonly int[] FontSizes = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 48];
    internal static readonly string[] LineHeights = ["normal", "1", "1.15", "1.5", "2"];

    private static readonly (Func<string> Name, string Hex)[] TextColors =
    [
        (() => Translator.ComposerParity_ColorAutomatic, string.Empty), (() => Translator.ComposerParity_ColorBlack, "#000000"),
        (() => Translator.ComposerParity_ColorGray, "#666666"), (() => Translator.ComposerParity_ColorRed, "#c62828"),
        (() => Translator.ComposerParity_ColorOrange, "#ef6c00"), (() => Translator.ComposerParity_ColorYellow, "#f9a825"),
        (() => Translator.ComposerParity_ColorGreen, "#2e7d32"), (() => Translator.ComposerParity_ColorBlue, "#1565c0"),
        (() => Translator.ComposerParity_ColorPurple, "#6a1b9a")
    ];

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
    private readonly Action<Exception> _error;
    private readonly Gtk.Overlay _root = Gtk.Overlay.New();
    private readonly Gtk.Box _bar = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.DrawingArea _probe = Gtk.DrawingArea.New();
    private readonly Gtk.Stack? _stack;
    private readonly Gtk.StackSwitcher? _switcher;
    private readonly EditorOverflowRow[] _rows;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<string, EditorFormatButton> _toggles = new(StringComparer.Ordinal);
    private readonly List<EditorFormatButton> _buttons = [];
    private readonly List<Gtk.Popover> _popovers = [];
    private readonly List<EditorToolbarItem> _formatItems = [];
    private readonly List<EditorToolbarItem> _insertItems = [];
    private readonly List<EditorToolbarItem> _optionsItems = [];
    private readonly List<EditorToolbarItem> _trailingItems = [];
    private readonly Gtk.DropDown _fontDropDown;
    private readonly Gtk.DropDown _sizeDropDown;
    private readonly Gtk.DropDown? _paragraphDropDown;
    private readonly MainLoop.Coalescer _relayout;
    private readonly string[] _fonts;
    private WeakReference<WinoMenuPopover>? _lastMenu;
    private EditorState _state = new();
    private int _width = -1;
    private bool _applying;
    private bool _disposed;

    public EditorFormatToolbar(EditorToolbarLayout layout, EditorToolbarFeatures features, IPreferencesService? preferences, Action<Exception> error)
    {
        WinoLiveObjects.Track(this);
        _layout = layout;
        _error = error;
        _relayout = new MainLoop.Coalescer(ApplyWidth);

        var composerFont = preferences?.ComposerFont;
        _fonts = string.IsNullOrWhiteSpace(composerFont) || Fonts.Contains(composerFont, StringComparer.OrdinalIgnoreCase) ? Fonts : [.. Fonts, composerFont];
        _fontDropDown = DropDown(Translator.Composer_Font, _fonts);
        _subscriptions.Add(GtkSignals.OnNotify(_fontDropDown, "selected", () =>
        {
            if (!_applying && Selected(_fontDropDown, _fonts.Length) is { } index) Run(EditorCommand.SetFontFamily(_fonts[index]));
        }));
        _sizeDropDown = DropDown(Translator.Composer_FontSize, FontSizes.Select(static size => size.ToString(CultureInfo.InvariantCulture)).ToArray());
        _subscriptions.Add(GtkSignals.OnNotify(_sizeDropDown, "selected", () =>
        {
            if (!_applying && Selected(_sizeDropDown, FontSizes.Length) is { } index) Run(EditorCommand.SetFontSize(FontSizes[index]));
        }));
        SelectTypography(preferences?.ComposerFont, preferences?.ComposerFontSize);

        _formatItems.Add(new EditorToolbarItem(_fontDropDown, menu => menu.Submenu(Translator.Composer_Font, sub =>
        {
            foreach (var font in _fonts) sub.Radio("font", font, IsCurrentFont(font), () => Run(EditorCommand.SetFontFamily(font)));
        }, icon: WinoIconGlyph.TextFont)));
        _formatItems.Add(new EditorToolbarItem(_sizeDropDown, menu => menu.Submenu(Translator.Composer_FontSize, sub =>
        {
            foreach (var size in FontSizes) sub.Radio("size", size.ToString(CultureInfo.InvariantCulture), _state.FontSize == size, () => Run(EditorCommand.SetFontSize(size)));
        }, icon: WinoIconGlyph.TextFont)));
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(Toggle("bold", WinoIconGlyph.TextBold, Translator.Composer_Bold, EditorCommand.ToggleBold, "<Control>b", static s => s.IsBold));
        _formatItems.Add(Toggle("italic", WinoIconGlyph.TextItalic, Translator.Composer_Italic, EditorCommand.ToggleItalic, "<Control>i", static s => s.IsItalic));
        _formatItems.Add(Toggle("underline", WinoIconGlyph.TextUnderline, Translator.Composer_Underline, EditorCommand.ToggleUnderline, "<Control>u", static s => s.IsUnderline));
        _formatItems.Add(Toggle("strike", WinoIconGlyph.TextStrikethrough, Translator.Composer_Strikethrough, EditorCommand.ToggleStrikethrough, null, static s => s.IsStrikethrough));
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(ColorItem(WinoIconGlyph.TextColor, Translator.Composer_TextColor, TextColors, false));
        if (features.Highlight) _formatItems.Add(ColorItem(WinoIconGlyph.Highlight, Translator.Composer_HighlightColor, HighlightColors, true));
        if (features.ClearFormatting) _formatItems.Add(Command(WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, () => Run(EditorCommand.ClearFormatting())));
        if (features.Paragraph)
        {
            _paragraphDropDown = DropDown(Translator.Composer_ParagraphStyle, ParagraphStyles.Select(static style => style.Name()).ToArray());
            _subscriptions.Add(GtkSignals.OnNotify(_paragraphDropDown, "selected", () =>
            {
                if (!_applying && _paragraphDropDown is { } dropDown && Selected(dropDown, ParagraphStyles.Length) is { } index)
                    Run(EditorCommand.SetParagraphStyle(ParagraphStyles[index].Tag));
            }));
            _formatItems.Add(EditorToolbarItem.Divider());
            _formatItems.Add(new EditorToolbarItem(_paragraphDropDown, menu => menu.Submenu(Translator.Composer_ParagraphStyle, sub =>
            {
                foreach (var style in ParagraphStyles)
                    sub.Radio("paragraph", style.Name(), string.Equals(_state.ParagraphStyle ?? "p", style.Tag, StringComparison.OrdinalIgnoreCase), () => Run(EditorCommand.SetParagraphStyle(style.Tag)));
            }, icon: WinoIconGlyph.TextParagraphDirection)));
        }
        _formatItems.Add(EditorToolbarItem.Divider());
        _formatItems.Add(Toggle("bullets", WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList, null, static s => s.IsUnorderedList));
        _formatItems.Add(Toggle("numbers", WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList, null, static s => s.IsOrderedList));
        // Outdent stays enabled: the editor never reports whether it can outdent (Mac and Windows likewise).
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

        if (layout == EditorToolbarLayout.Tabbed)
        {
            _rows = [new EditorOverflowRow(), new EditorOverflowRow(), new EditorOverflowRow()];
            var stack = _stack = Gtk.Stack.New();
            stack.SetHhomogeneous(false);
            stack.SetHexpand(true);
            stack.SetTransitionType(Gtk.StackTransitionType.None);
            stack.AddTitled(_rows[0].Root, "format", Translator.EditorToolbarOption_Format);
            stack.AddTitled(_rows[1].Root, "insert", Translator.EditorToolbarOption_Insert);
            stack.AddTitled(_rows[2].Root, "options", Translator.EditorToolbarOption_Options);
            _switcher = Gtk.StackSwitcher.New();
            _switcher.SetStack(stack);
            _switcher.SetHalign(Gtk.Align.Start);
            _switcher.SetHexpand(false);
            WinoA11y.Label(_switcher, Translator.Composer_EditorToolbarLabel);
            _subscriptions.Add(GtkSignals.OnNotify(stack, "visible-child", _relayout.Request));
            _bar.Append(_switcher);
            _bar.Append(stack);
        }
        else
        {
            _rows = [new EditorOverflowRow()];
            _rows[0].Root.SetHexpand(true);
            _bar.Append(_rows[0].Root);
        }

        _bar.AddCssClass("wino-editor-toolbar");
        _probe.SetHalign(Gtk.Align.Fill);
        _probe.SetValign(Gtk.Align.Start);
        _probe.SetContentHeight(1);
        _probe.SetCanTarget(false);
        _probe.SetCanFocus(false);
        _root.SetChild(_bar);
        _root.AddOverlay(_probe);
        // resize runs during allocation: only the width is stored there, the rows change on the next turn.
        _subscriptions.Add(GtkSignals.Connect<Gtk.DrawingArea, Gtk.DrawingArea.ResizeSignalArgs>(h => _probe.OnResize += h, h => _probe.OnResize -= h, (_, args) =>
        {
            if (args.Width == _width) return;
            _width = args.Width;
            _relayout.Request();
        }));
        Rebuild();
    }

    public Gtk.Widget Root => _root;
    public WebKitGtkHtmlMailEditorSession? Session { get; private set; }
    public EditorState CurrentState => _state;

    public event Action<EditorShortcutKind>? ShortcutRequested;
    public event Action? ImageInsertionRequested;
    public event Action<EditorState>? StateApplied;

    public void Attach(WebKitGtkHtmlMailEditorSession session)
    {
        Detach();
        Session = session;
        session.StateChanged += SessionStateChanged;
        session.ShortcutRequested += SessionShortcutRequested;
        session.ImageInsertionRequested += SessionImageInsertionRequested;
        ApplyState(session.CurrentState);
    }

    public void Detach()
    {
        if (Session is not { } session) return;
        session.StateChanged -= SessionStateChanged;
        session.ShortcutRequested -= SessionShortcutRequested;
        session.ImageInsertionRequested -= SessionImageInsertionRequested;
        Session = null;
    }

    public void Run(EditorCommand command)
    {
        if (_disposed || Session is not { } session) return;
        Observe(RunAsync(session, command));
    }

    public async Task RunAsync(WebKitGtkHtmlMailEditorSession session, EditorCommand command)
    {
        await session.ExecuteCommandAsync(command);
        if (!_disposed && ReferenceEquals(Session, session)) await session.FocusEditorAsync(true);
    }

    public void AddInsertItems(params EditorToolbarItem[] items)
    {
        _insertItems.AddRange(items);
        Rebuild();
    }

    /// <summary>Host commands at the start of the Insert commands (Windows InsertCustomContent: Attach files).</summary>
    public void PrependInsertItems(params EditorToolbarItem[] items)
    {
        _insertItems.InsertRange(0, items);
        Rebuild();
    }

    public void AddOptionsItems(params EditorToolbarItem[] items)
    {
        _optionsItems.AddRange(items);
        Rebuild();
    }

    public void AddTrailingItem(EditorToolbarItem item)
    {
        _trailingItems.Add(item);
        Rebuild();
    }

    /// <summary>Re-measures the rows after a host item changed its width or availability.</summary>
    public void InvalidateOverflow()
    {
        foreach (var row in _rows) row.Layout();
    }

    /// <summary>Makes a host button look like the toolbar's own commands.</summary>
    public static EditorToolbarItem Item(EditorFormatButton button, Action<WinoMenuBuilder>? menuItem = null, Func<bool>? isAvailable = null)
        => new(button.Root, menuItem ?? (menu => menu.Item(button.Label, () => button.Root.Activate(), button.Enabled, icon: button.Glyph)), isAvailable);

    private void SessionStateChanged(object? sender, EditorState state) => ApplyState(state);

    private void SessionShortcutRequested(object? sender, EditorShortcutKind kind) => ShortcutRequested?.Invoke(kind);

    private void SessionImageInsertionRequested(object? sender, EventArgs args) => ImageInsertionRequested?.Invoke();

    private void Rebuild()
    {
        if (_layout == EditorToolbarLayout.Tabbed && _stack is { } stack)
        {
            _rows[0].SetItems(_formatItems);
            _rows[1].SetItems(_insertItems);
            _rows[2].SetItems(Join(_optionsItems, _trailingItems));
            stack.GetPage(_rows[1].Root).SetVisible(!_rows[1].IsEmpty);
            stack.GetPage(_rows[2].Root).SetVisible(!_rows[2].IsEmpty);
        }
        else
        {
            _rows[0].SetItems(Join(_formatItems, _insertItems, _optionsItems, _trailingItems));
        }
        _relayout.Request();
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

    private void ApplyWidth()
    {
        if (_disposed || _width < 0) return;
        var width = _width;
#if DEBUG
        if (DebugWidth > 0) width = DebugWidth;
#endif
        foreach (var row in _rows) row.SetAvailableWidth(Math.Max(0, width));
    }

    private void ApplyState(EditorState state)
    {
        if (_disposed) return;
        _state = state;
        _applying = true;
        try
        {
            SetActive("bold", state.IsBold);
            SetActive("italic", state.IsItalic);
            SetActive("underline", state.IsUnderline);
            SetActive("strike", state.IsStrikethrough);
            SetActive("bullets", state.IsUnorderedList);
            SetActive("numbers", state.IsOrderedList);
            SetAlignmentActive(state.Alignment);
            SelectTypography(state.FontFamily, state.FontSize);
            if (_paragraphDropDown is { } paragraph)
            {
                var index = Array.FindIndex(ParagraphStyles, style => string.Equals(style.Tag, state.ParagraphStyle ?? "p", StringComparison.OrdinalIgnoreCase));
                if (index >= 0) paragraph.SetSelected((uint)index);
            }
        }
        finally { _applying = false; }
        StateApplied?.Invoke(state);
    }

    private void SetActive(string key, bool active)
    {
        if (_toggles.TryGetValue(key, out var button)) button.IsActive = active;
    }

    private void SetAlignmentActive(EditorTextAlignment alignment)
    {
        SetActive("left", alignment == EditorTextAlignment.Left);
        SetActive("center", alignment == EditorTextAlignment.Center);
        SetActive("right", alignment == EditorTextAlignment.Right);
        SetActive("justify", alignment == EditorTextAlignment.Justify);
    }

    private void SelectTypography(string? fontFamily, int? size)
    {
        var applying = _applying;
        _applying = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(fontFamily))
            {
                var index = Array.FindIndex(_fonts, font => string.Equals(font, NormalizeFamily(fontFamily), StringComparison.OrdinalIgnoreCase));
                if (index >= 0) _fontDropDown.SetSelected((uint)index);
            }
            if (size is { } points)
            {
                var index = Array.IndexOf(FontSizes, points);
                if (index >= 0) _sizeDropDown.SetSelected((uint)index);
            }
        }
        finally { _applying = applying; }
    }

    private static string NormalizeFamily(string family) => family.Split(',')[0].Trim().Trim('"', '\'');

    private bool IsCurrentFont(string font)
        => !string.IsNullOrWhiteSpace(_state.FontFamily) && string.Equals(NormalizeFamily(_state.FontFamily), font, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The editor reports the computed line height in pixels; the preset is the one whose ratio to the font size is within
    /// 0.05. "normal" matches the Default preset (Mac <c>MatchLineHeight</c>).
    /// </summary>
    internal static string? MatchLineHeight(string? computed, int? fontSize)
    {
        if (string.IsNullOrWhiteSpace(computed)) return null;
        var value = computed.Trim();
        if (string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase)) return "normal";
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            if (fontSize is not > 0 || !double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels)) return null;
            var ratio = pixels / fontSize.Value;
            foreach (var preset in LineHeights.Skip(1))
                if (Math.Abs(double.Parse(preset, CultureInfo.InvariantCulture) - ratio) <= 0.05) return preset;
            return null;
        }
        return LineHeights.FirstOrDefault(preset => string.Equals(preset, value, StringComparison.OrdinalIgnoreCase));
    }

    private EditorToolbarItem Toggle(string key, WinoIconGlyph glyph, string label, Func<EditorCommand> command, string? accelerator, Func<EditorState, bool> isOn)
    {
        var button = Track(new EditorFormatButton(glyph, label, () => Run(command()), toggle: true, Tooltip(label, accelerator)));
        _toggles[key] = button;
        return new EditorToolbarItem(button.Root, menu => menu.Check(label, isOn(_state), _ => Run(command()), true, accelerator, glyph));
    }

    private EditorToolbarItem Alignment(string key, WinoIconGlyph glyph, string label, EditorTextAlignment alignment)
    {
        // A choice, not a switch: clicking the active alignment keeps it.
        var button = Track(new EditorFormatButton(glyph, label, () =>
        {
            _applying = true;
            try { SetAlignmentActive(alignment); }
            finally { _applying = false; }
            Run(EditorCommand.SetAlignment(alignment));
        }, toggle: true));
        _toggles[key] = button;
        return new EditorToolbarItem(button.Root, menu => menu.Radio("alignment", label, _state.Alignment == alignment, () => Run(EditorCommand.SetAlignment(alignment)), icon: glyph));
    }

    private EditorToolbarItem Command(WinoIconGlyph glyph, string label, Action action)
    {
        var button = Track(new EditorFormatButton(glyph, label, action));
        return new EditorToolbarItem(button.Root, menu => menu.Item(label, action, icon: glyph));
    }

    private EditorToolbarItem LineSpacingItem()
    {
        EditorFormatButton? button = null;
        button = Track(new EditorFormatButton(WinoIconGlyph.TextLineSpacing, Translator.Composer_LineSpacing, () =>
        {
            if (button is null) return;
            var menu = WinoMenus.Create();
            FillLineSpacing(menu);
            _lastMenu = new WeakReference<WinoMenuPopover>(ContextMenu.Show(button.Root, menu.Build()));
        }));
        return new EditorToolbarItem(button.Root, menu => menu.Submenu(Translator.Composer_LineSpacing, FillLineSpacing, icon: WinoIconGlyph.TextLineSpacing));
    }

    private void FillLineSpacing(WinoMenuBuilder menu)
    {
        var current = MatchLineHeight(_state.LineHeight, _state.FontSize);
        foreach (var preset in LineHeights)
        {
            var value = preset;
            menu.Radio("line-height", value == "normal" ? Translator.Composer_LineSpacingDefault : value, current == value, () => Run(EditorCommand.SetLineHeight(value)));
        }
    }

    private EditorToolbarItem ColorItem(WinoIconGlyph glyph, string label, (Func<string> Name, string Hex)[] colors, bool highlight)
    {
        var button = Gtk.MenuButton.New();
        button.SetChild(WinoIcons.Label(glyph));
        button.SetHasFrame(false);
        button.SetValign(Gtk.Align.Center);
        button.AddCssClass("wino-editor-tool");
        WinoA11y.LabelWithTooltip(button, label);

        var popover = Gtk.Popover.New();
        var grid = Gtk.Grid.New();
        grid.SetRowSpacing(4);
        grid.SetColumnSpacing(4);
        grid.SetMarginTop(6);
        grid.SetMarginBottom(6);
        grid.SetMarginStart(6);
        grid.SetMarginEnd(6);
        var column = 0;
        var row = 1;
        foreach (var (name, hex) in colors)
        {
            var value = hex;
            var title = name();
            Gtk.Button swatch;
            if (string.IsNullOrEmpty(hex))
            {
                swatch = Gtk.Button.NewWithLabel(title);
                swatch.AddCssClass("flat");
                grid.Attach(swatch, 0, 0, 5, 1);
            }
            else
            {
                swatch = Gtk.Button.New();
                var chip = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
                chip.SetSizeRequest(20, 20);
                chip.AddCssClass("wino-color-swatch");
                if (WinoTint.FillClassFor(hex, null) is { } fill) chip.AddCssClass(fill);
                swatch.SetChild(chip);
                swatch.AddCssClass("flat");
                WinoA11y.LabelWithTooltip(swatch, title);
                grid.Attach(swatch, column, row, 1, 1);
                if (++column == 5)
                {
                    column = 0;
                    row++;
                }
            }
            WinoA11y.Rename(swatch, title);
            _subscriptions.Add(GtkSignals.OnClicked(swatch, () =>
            {
                popover.Popdown();
                Run(highlight ? EditorCommand.SetHighlightColor(value) : EditorCommand.SetTextColor(value));
            }));
        }
        popover.SetChild(grid);
        button.SetPopover(popover);
        _popovers.Add(popover);

        return new EditorToolbarItem(button, menu => menu.Submenu(label, sub =>
        {
            var current = highlight ? _state.HighlightColor : _state.TextColor;
            foreach (var (name, hex) in colors)
            {
                var value = hex;
                sub.Radio(highlight ? "highlight" : "color", name(), SameColor(current, hex),
                    () => Run(highlight ? EditorCommand.SetHighlightColor(value) : EditorCommand.SetTextColor(value)),
                    icon: string.IsNullOrEmpty(hex) ? default : new WinoMenuIcon(WinoIcons.Glyph(WinoIconGlyph.Circle), hex));
            }
        }, icon: glyph));
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

    private static Gtk.DropDown DropDown(string label, string[] items)
    {
        var dropDown = Gtk.DropDown.NewFromStrings(items);
        dropDown.SetValign(Gtk.Align.Center);
        dropDown.AddCssClass("flat");
        dropDown.SetTooltipText(label);
        WinoA11y.Label(dropDown, label);
        return dropDown;
    }

    private static int? Selected(Gtk.DropDown dropDown, int count)
    {
        var selected = dropDown.GetSelected();
        return selected < count ? (int)selected : null;
    }

    private static string Tooltip(string label, string? accelerator)
        => accelerator is not null && ShortcutTooltip(accelerator) is { } keys ? $"{label} ({keys})" : label;

    private static string? ShortcutTooltip(string accelerator)
        => Infrastructure.ShortcutKeys.TryParseAccelerator(accelerator, out var key, out var modifiers) ? Gtk.Functions.AcceleratorGetLabel(key, modifiers) : null;

    private EditorFormatButton Track(EditorFormatButton button)
    {
        _buttons.Add(button);
        return button;
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) _error(exception); }
    }

#if DEBUG
    internal int DebugWidth { get; set; }

    internal void DebugRelayout() => ApplyWidth();

    internal void DebugRaiseShortcut(EditorShortcutKind kind) => ShortcutRequested?.Invoke(kind);

    internal WinoMenuPopover? DebugLastMenu => _lastMenu is { } weak && weak.TryGetTarget(out var menu) ? menu : null;

    internal void SelectTab(int index)
    {
        if (_stack is null) return;
        _stack.SetVisibleChild(_rows[Math.Clamp(index, 0, _rows.Length - 1)].Root);
    }

    internal EditorOverflowRow VisibleRow => _stack?.GetVisibleChild() is { } child ? _rows.FirstOrDefault(row => ReferenceEquals(row.Root, child)) ?? _rows[0] : _rows[0];

    internal IEnumerable<EditorToolbarItem> AllItems => _rows.SelectMany(static row => row.Items);

    internal bool DebugSelect(string name, int index)
    {
        var dropDown = name switch
        {
            "font" => _fontDropDown,
            "size" => _sizeDropDown,
            "paragraph" => _paragraphDropDown,
            _ => null
        };
        if (dropDown is null) return false;
        dropDown.SetSelected((uint)index);
        return true;
    }

    internal string Describe()
    {
        var lines = new List<string>();
        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            var visible = row.Items.Where(static item => item.Widget.GetVisible()).Select(static item =>
            {
                item.Widget.Measure(Gtk.Orientation.Horizontal, -1, out _, out var natural, out _, out _);
                return $"{item.Name}:{natural}/{item.Widget.GetWidth()}";
            });
            var overflow = row.OverflowItems.Select(static item => item.Name);
            var shown = _stack is null || ReferenceEquals(_stack.GetVisibleChild(), row.Root);
            row.MoreButton.Measure(Gtk.Orientation.Horizontal, -1, out _, out var more, out _, out _);
            lines.Add($"row{index} shown={shown} moreWidth={more} rowWidth={row.Root.GetWidth()} more={row.IsOverflowing} visible=[{string.Join(", ", visible)}] overflow=[{string.Join(", ", overflow)}]");
        }
        foreach (var (key, button) in _toggles) lines.Add($"toggle {key} active={button.IsActive} enabled={button.Enabled}");
        lines.Add($"font={_fontDropDown.GetSelected()} size={_sizeDropDown.GetSelected()} paragraph={_paragraphDropDown?.GetSelected()} width={_width} debugWidth={DebugWidth}");
        lines.Add($"state bold={_state.IsBold} italic={_state.IsItalic} color={_state.TextColor} highlight={_state.HighlightColor} paragraph={_state.ParagraphStyle} align={_state.Alignment} lineHeight={_state.LineHeight} font={_state.FontFamily} size={_state.FontSize}");
        return string.Join('\n', lines);
    }
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _relayout.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        foreach (var button in _buttons) button.Dispose();
        _buttons.Clear();
        foreach (var popover in _popovers) popover.Popdown();
        if (_lastMenu is { } last && last.TryGetTarget(out var menu)) menu.Popdown();
        _popovers.Clear();
        foreach (var row in _rows) row.Dispose();
        ShortcutRequested = null;
        ImageInsertionRequested = null;
        StateApplied = null;
    }
}
