using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Editor.AppKit;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Views.Mail;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Dialogs;

/// <summary>Which optional toolbar commands a <see cref="WinoRichTextEditorView"/> shows, and whether the toolbar is fused to the editor.</summary>
internal sealed record WinoRichTextEditorOptions(
    bool Fused = false,
    bool Highlight = false,
    bool Table = false,
    bool Emoji = false,
    bool ClearFormatting = true);

/// <summary>
/// The one-row Format toolbar (composer vocabulary: font and size pop-ups, B/I/U/S, colours, lists and
/// indent, alignment, link/image/…) over the composer's own WKWebView HTML editor session. Shared by the
/// signature editor sheet and the email template editor page. The caller sizes <see cref="EditorSurface"/>,
/// calls <see cref="LoadAsync"/> once the view is in a window, reads HTML with <see cref="GetHtmlBodyAsync"/>
/// and disposes the session with <see cref="DisposeAsync"/>. Use on the main thread.
/// </summary>
internal sealed class WinoRichTextEditorView : NSView
{
    /// <summary>The Windows editor text colours (WinoMailEditor capabilities), without "Default".</summary>
    private static readonly (string Name, string Hex)[] TextColors =
    [
        ("Black", "#000000"), ("Gray", "#666666"), ("Red", "#c62828"), ("Orange", "#ef6c00"),
        ("Yellow", "#f9a825"), ("Green", "#2e7d32"), ("Blue", "#1565c0"), ("Purple", "#6a1b9a")
    ];

    /// <summary>The Windows editor highlight colours; "None" clears the highlight.</summary>
    private static readonly (string Name, string Hex)[] HighlightColors =
    [
        ("None", string.Empty), ("Yellow", "#fff59d"), ("Green", "#c8e6c9"),
        ("Blue", "#bbdefb"), ("Pink", "#f8bbd0"), ("Orange", "#ffe0b2")
    ];

    private static readonly string[] Fonts = ComposePageViewController.Fonts;
    private static readonly int[] FontSizes = ComposePageViewController.FontSizes;

    private readonly NSPopUpButton _fontPopup;
    private readonly NSPopUpButton _sizePopup;
    private readonly WKWebView _webView;
    private readonly AppKitHtmlMailEditorSession _editor;
    private readonly Dictionary<string, FormatButton> _toggles = [];
    private readonly IPreferencesService? _preferences;
    private readonly Action<Exception>? _error;
    private bool _disposed;

    public WinoRichTextEditorView(WinoRichTextEditorOptions options, string accessibilityLabel, IExternalLauncher? launcher, IPreferencesService? preferences, Action<Exception>? error)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _preferences = preferences;
        _error = error;

        // ---- Format toolbar ----
        _fontPopup = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _fontPopup.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize);
        _fontPopup.AddItems(Fonts);
        _fontPopup.WidthAnchor.ConstraintEqualTo(118).Active = true;
        _fontPopup.Activated += (_, _) => Run(EditorCommand.SetFontFamily(_fontPopup.TitleOfSelectedItem));
        WinoAccessibility.Label(_fontPopup, Translator.Composer_Font);
        _fontPopup.ToolTip = Translator.Composer_Font;

        _sizePopup = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _sizePopup.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize);
        _sizePopup.AddItems(FontSizes.Select(static size => size.ToString()).ToArray());
        _sizePopup.WidthAnchor.ConstraintEqualTo(52).Active = true;
        _sizePopup.Activated += (_, _) => Run(EditorCommand.SetFontSize(FontSizes[Math.Max(0, (int)_sizePopup.IndexOfSelectedItem)]));
        WinoAccessibility.Label(_sizePopup, Translator.Composer_FontSize);
        _sizePopup.ToolTip = Translator.Composer_FontSize;
        SelectTypography(preferences?.ComposerFont, preferences?.ComposerFontSize);

        var bold = Toggle("bold", WinoIconGlyph.TextBold, Translator.Composer_Bold, EditorCommand.ToggleBold);
        var italic = Toggle("italic", WinoIconGlyph.TextItalic, Translator.Composer_Italic, EditorCommand.ToggleItalic);
        var underline = Toggle("underline", WinoIconGlyph.TextUnderline, Translator.Composer_Underline, EditorCommand.ToggleUnderline);
        var strike = Toggle("strike", WinoIconGlyph.TextStrikethrough, Translator.Composer_Strikethrough, EditorCommand.ToggleStrikethrough);
        FormatButton? color = null;
        color = new FormatButton(WinoIconGlyph.TextColor, Translator.Composer_TextColor, () => ShowColorMenu(color!, TextColors, EditorCommand.SetTextColor));
        FormatButton? highlight = null;
        highlight = new FormatButton(WinoIconGlyph.Highlight, Translator.Composer_HighlightColor, () => ShowColorMenu(highlight!, HighlightColors, EditorCommand.SetHighlightColor));
        var bullets = Toggle("bullets", WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList);
        var numbers = Toggle("numbers", WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList);
        var outdent = new FormatButton(WinoIconGlyph.TextIndentDecrease, Translator.Composer_Outdent, () => Run(EditorCommand.Outdent()));
        var indent = new FormatButton(WinoIconGlyph.TextIndentIncrease, Translator.Composer_Indent, () => Run(EditorCommand.Indent()));
        var left = Toggle("left", WinoIconGlyph.TextAlignLeft, Translator.Composer_AlignLeft, () => EditorCommand.SetAlignment(EditorTextAlignment.Left));
        var center = Toggle("center", WinoIconGlyph.TextAlignCenter, Translator.Composer_AlignCenter, () => EditorCommand.SetAlignment(EditorTextAlignment.Center));
        var right = Toggle("right", WinoIconGlyph.TextAlignRight, Translator.Composer_AlignRight, () => EditorCommand.SetAlignment(EditorTextAlignment.Right));
        var link = new FormatButton(WinoIconGlyph.Link, Translator.Composer_InsertLink, () => Observe(InsertLinkAsync()));
        var image = new FormatButton(WinoIconGlyph.Image, Translator.Composer_InsertPicture, () => Run(EditorCommand.InsertImage()));
        var table = new FormatButton(WinoIconGlyph.Table, Translator.Composer_InsertTable, () => Run(EditorCommand.InsertTable(new EditorTableCommandArgs(3, 3))));
        var emoji = new FormatButton(WinoIconGlyph.Emoji, Translator.Composer_InsertEmojiDescription, ShowEmojiPalette);
        var clear = new FormatButton(WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, () => Run(EditorCommand.ClearFormatting()));
        left.IsActive = true;

        var items = new List<NSView> { _fontPopup, _sizePopup, Divider(), bold, italic, underline, strike, color };
        if (options.Highlight) items.Add(highlight);
        items.AddRange([Divider(), bullets, numbers, outdent, indent, Divider(), left, center, right, Divider(), link, image]);
        if (options.Table) items.Add(table);
        if (options.Emoji) items.Add(emoji);
        if (options.ClearFormatting) items.Add(clear);

        var toolbarItems = WinoLayout.HStack(1, items.ToArray());
        toolbarItems.EdgeInsets = new NSEdgeInsets(0, options.Fused ? 6 : 4, 0, 4);
        toolbarItems.SetCustomSpacing(4, _fontPopup);
        toolbarItems.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var toolbar = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.ControlRadius,
            Fill = options.Fused ? WinoSettingsStyle.CardFill : WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.06)),
            Stroke = options.Fused ? EditorStroke : WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (options.Fused) toolbar.Corners = CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner;
        toolbar.AddSubview(toolbarItems);
        NSLayoutConstraint.ActivateConstraints(
        [
            toolbar.HeightAnchor.ConstraintEqualTo(36),
            toolbarItems.LeadingAnchor.ConstraintEqualTo(toolbar.LeadingAnchor),
            toolbarItems.TrailingAnchor.ConstraintLessThanOrEqualTo(toolbar.TrailingAnchor),
            toolbarItems.CenterYAnchor.ConstraintEqualTo(toolbar.CenterYAnchor)
        ]);
        toolbar.AccessibilityElement = true;
        toolbar.AccessibilityRole = NSAccessibilityRoles.ToolbarRole;
        toolbar.AccessibilityLabel = Translator.EditorToolbarOption_Format;
        toolbar.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Vertical);

        // ---- Editor (the composer's WKWebView editor session) ----
        _webView = new WKWebView(CGRect.Empty, new WKWebViewConfiguration()) { TranslatesAutoresizingMaskIntoConstraints = false };
        _webView.SetValueForKey(NSNumber.FromBoolean(false), new NSString("drawsBackground"));
        WinoAccessibility.Label(_webView, accessibilityLabel);
        EditorSurface = new WinoSurfaceView
        {
            CornerRadius = options.Fused ? 7 : WinoStyle.ControlRadius,
            Fill = WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0x1E1E1E)),
            Stroke = EditorStroke,
            StrokeWidth = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (options.Fused) EditorSurface.Corners = CACornerMask.MinXMaxYCorner | CACornerMask.MaxXMaxYCorner;
        EditorSurface.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        WinoLayout.Fill(_webView, EditorSurface, 6, 8, 6, 8);

        _editor = new AppKitHtmlMailEditorSession(_webView);
        if (launcher is not null) _editor.Configure(launcher);
        _editor.OperationFailed += (_, exception) => _error?.Invoke(exception);
        _editor.StateChanged += (_, state) => OnMain(() => ApplyState(state));
        _editor.ImageInsertionRequested += (_, _) => OnMain(() => Observe(PickImagesAsync()));
        _editor.ShortcutRequested += (_, kind) => { if (kind == EditorShortcutKind.OpenLinkDialog) OnMain(() => Observe(InsertLinkAsync())); };

        // The editor is added last so that, when fused, its top hairline draws over the toolbar's bottom edge.
        AddSubview(toolbar);
        AddSubview(EditorSurface);
        NSLayoutConstraint.ActivateConstraints(
        [
            toolbar.TopAnchor.ConstraintEqualTo(TopAnchor),
            toolbar.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            toolbar.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            // Fused: the editor's top hairline replaces the toolbar's bottom one.
            EditorSurface.TopAnchor.ConstraintEqualTo(toolbar.BottomAnchor, options.Fused ? -1 : 8),
            EditorSurface.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            EditorSurface.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            EditorSurface.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
    }

    private static NSColor EditorStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.10), WinoStyle.Hex(0xFFFFFF, 0.10));

    /// <summary>The rounded editor surface below the toolbar; the caller sets its height.</summary>
    public WinoSurfaceView EditorSurface { get; }

    /// <summary>The web view that receives keyboard focus.</summary>
    public NSView EditorView => _webView;

    /// <summary>True once <see cref="LoadAsync"/> rendered the initial HTML.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Raised on the main thread when <see cref="IsReady"/> becomes true.</summary>
    public event EventHandler? ReadyChanged;

    /// <summary>Raised when the editor content changes.</summary>
    public event EventHandler? ContentChanged
    {
        add => _editor.ContentChanged += value;
        remove => _editor.ContentChanged -= value;
    }

    /// <summary>Initialises the editor session, applies the theme and composer typography, then renders <paramref name="html"/>.</summary>
    public async Task LoadAsync(string? html, bool dark)
    {
        await _editor.InitializeAsync();
        await _editor.SetThemeAsync(dark);
        if (_preferences is not null) await _editor.SetDefaultTypographyAsync(_preferences.ComposerFont, _preferences.ComposerFontSize);
        await _editor.RenderHtmlAsync(html ?? string.Empty);
        OnMain(() =>
        {
            if (_disposed) return;
            IsReady = true;
            ReadyChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public Task<string?> GetHtmlBodyAsync() => _editor.GetHtmlBodyAsync();

    /// <summary>Whether <paramref name="appearance"/> (a window's or view's effective appearance) is dark.</summary>
    public static bool IsDark(NSAppearance appearance)
        => appearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]) == NSAppearance.NameDarkAqua.ToString();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ReadyChanged = null;
        await _editor.DisposeAsync();
    }

    // ---- Toolbar ----

    private FormatButton Toggle(string key, WinoIconGlyph glyph, string label, Func<EditorCommand> command)
    {
        var button = new FormatButton(glyph, label, () => Run(command()));
        _toggles[key] = button;
        return button;
    }

    private static NSView Divider()
    {
        var divider = new WinoSeparator(vertical: true);
        divider.HeightAnchor.ConstraintEqualTo(18).Active = true;
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(divider);
        NSLayoutConstraint.ActivateConstraints(
        [
            host.WidthAnchor.ConstraintEqualTo(9),
            host.HeightAnchor.ConstraintEqualTo(18),
            divider.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            divider.CenterYAnchor.ConstraintEqualTo(host.CenterYAnchor)
        ]);
        return host;
    }

    private void ApplyState(EditorState state)
    {
        if (_disposed) return;
        SetActive("bold", state.IsBold);
        SetActive("italic", state.IsItalic);
        SetActive("underline", state.IsUnderline);
        SetActive("strike", state.IsStrikethrough);
        SetActive("bullets", state.IsUnorderedList);
        SetActive("numbers", state.IsOrderedList);
        SetActive("left", state.Alignment is EditorTextAlignment.Left or EditorTextAlignment.Justify);
        SetActive("center", state.Alignment == EditorTextAlignment.Center);
        SetActive("right", state.Alignment == EditorTextAlignment.Right);
        int? size = null;
        if (state.FontSize is { } value && int.TryParse(new string(value.ToString()!.TakeWhile(char.IsDigit).ToArray()), out var points)) size = points;
        SelectTypography(state.FontFamily, size);
    }

    private void SetActive(string key, bool active)
    {
        if (_toggles.TryGetValue(key, out var button)) button.IsActive = active;
    }

    private void SelectTypography(string? fontFamily, int? size)
    {
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            var family = fontFamily.Split(',')[0].Trim().Trim('"', '\'');
            int index = Array.FindIndex(Fonts, font => string.Equals(font, family, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _fontPopup.SelectItem(index);
        }
        if (size is { } points)
        {
            int index = Array.IndexOf(FontSizes, points);
            if (index >= 0) _sizePopup.SelectItem(index);
        }
    }

    private void ShowColorMenu(FormatButton anchor, (string Name, string Hex)[] colors, Func<string, EditorCommand> command)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        foreach (var (name, hex) in colors)
        {
            var swatch = string.IsNullOrEmpty(hex) ? null : WinoStyle.FromHexString(hex) ?? NSColor.Black;
            var item = new NSMenuItem(name, (_, _) => Run(command(hex)))
            {
                Image = NSImage.ImageWithSize(new CGSize(12, 12), false, rect =>
                {
                    var path = NSBezierPath.FromRoundedRect(rect.Inset(1, 1), 2, 2);
                    if (swatch is not null)
                    {
                        swatch.SetFill();
                        path.Fill();
                    }
                    else
                    {
                        NSColor.SecondaryLabel.SetStroke();
                        path.LineWidth = 1;
                        path.Stroke();
                    }
                    return true;
                })
            };
            menu.AddItem(item);
        }
        menu.PopUpMenu(null, new CGPoint(0, anchor.Bounds.Height + 4), anchor);
    }

    /// <summary>The macOS Character Viewer inserts the chosen emoji at the editor caret.</summary>
    private void ShowEmojiPalette()
    {
        if (_disposed || Window is not { } window) return;
        window.MakeFirstResponder(_webView);
        Observe(ShowEmojiPaletteAsync());
    }

    private async Task ShowEmojiPaletteAsync()
    {
        await _editor.FocusEditorAsync(true);
        OnMain(() => { if (!_disposed) NSApplication.SharedApplication.OrderFrontCharacterPalette(this); });
    }

    // ---- Link and image insertion (composer behaviour) ----

    private async Task InsertLinkAsync()
    {
        if (_disposed || Window is not { } window) return;
        var url = new NSTextField(new CGRect(0, 30, 300, 24)) { PlaceholderString = Translator.Composer_LinkUrlPlaceholder };
        var text = new NSTextField(new CGRect(0, 0, 300, 24)) { PlaceholderString = Translator.Composer_LinkTextPlaceholder, StringValue = _editor.CurrentState.SelectedText ?? string.Empty };
        var accessory = new NSView(new CGRect(0, 0, 300, 54));
        accessory.AddSubview(url);
        accessory.AddSubview(text);
        var alert = new NSAlert { MessageText = Translator.Composer_InsertLink, AccessoryView = accessory };
        alert.AddButton(Translator.Composer_InsertLink);
        alert.AddButton(Translator.Buttons_Cancel);
        alert.Window.InitialFirstResponder = url;
        var response = await alert.BeginSheetAsync(window);
        if ((long)response != 1000 || string.IsNullOrWhiteSpace(url.StringValue)) return;
        var address = url.StringValue.Trim();
        if (!address.Contains("://", StringComparison.Ordinal) && !address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) address = "https://" + address;
        Run(EditorCommand.InsertLink(new EditorLinkCommandArgs(address, string.IsNullOrWhiteSpace(text.StringValue) ? null : text.StringValue)));
    }

    /// <summary>Images are embedded as data URIs, like the composer's inline images.</summary>
    private async Task PickImagesAsync()
    {
        if (_disposed) return;
        var panel = NSOpenPanel.OpenPanel;
        panel.AllowsMultipleSelection = true;
        panel.CanChooseDirectories = false;
#pragma warning disable CA1422
        panel.AllowedFileTypes = ["png", "jpg", "jpeg", "gif", "webp", "heic", "bmp"];
#pragma warning restore CA1422
        if (panel.RunModal() != 1) return;
        var images = new List<EditorImageInfo>();
        foreach (var url in panel.Urls)
        {
            if (url.Path is not { } path) continue;
            var bytes = await File.ReadAllBytesAsync(path);
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = extension switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "webp" => "image/webp", "heic" => "image/heic", "bmp" => "image/bmp", _ => "image/png" };
            images.Add(new EditorImageInfo($"data:{mime};base64,{Convert.ToBase64String(bytes)}", Path.GetFileName(path)));
        }
        if (images.Count > 0 && !_disposed) await _editor.InsertImagesAsync(images);
    }

    // ---- Helpers ----

    private void Run(EditorCommand command)
    {
        if (_disposed) return;
        Observe(_editor.ExecuteCommandAsync(command));
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _error?.Invoke(exception); }
    }

    private static void OnMain(Action action) => NSApplication.SharedApplication.BeginInvokeOnMainThread(action);

    /// <summary>A 24×26 icon button whose pressed state is a subtle filled backplate (aria-pressed in the design).</summary>
    private sealed class FormatButton : WinoSurfaceView
    {
        private readonly NSButton _button;
        private bool _active;

        public FormatButton(WinoIconGlyph glyph, string label, Action action)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            CornerRadius = 5;
            _button = new NSButton
            {
                Bordered = false,
                BezelStyle = NSBezelStyle.Inline,
                Title = string.Empty,
                Image = WinoIcons.Image(glyph, 14, null, label),
                ImagePosition = NSCellImagePosition.ImageOnly,
                ContentTintColor = WinoStyle.PrimaryText,
                ToolTip = label,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            WinoAccessibility.Label(_button, label);
            _button.Activated += (_, _) => action();
            WinoLayout.Fill(_button, this);
            WinoLayout.Size(this, 24, 26);
        }

        public bool IsActive
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                Fill = value ? WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.09), WinoStyle.Hex(0xFFFFFF, 0.12)) : null;
                _button.AccessibilityValue = NSNumber.FromBoolean(value);
            }
        }
    }
}
