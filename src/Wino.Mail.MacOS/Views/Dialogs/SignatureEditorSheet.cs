using AppKit;
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

/// <summary>
/// The signature editor (Windows SignatureEditorDialog) as a 640pt window sheet: heading and account
/// line, the name field, a one-row Format toolbar in the composer vocabulary over the composer's own
/// HTML editor session, then the images hint, Cancel and Save. Save stays disabled while the name is
/// empty or the editor is still loading; Escape cancels and Cmd+Return saves (Return stays in the editor).
/// Build on the main thread, then call <see cref="PresentAsync"/> once.
/// </summary>
internal sealed class SignatureEditorSheet
{
    private const double Width = 640;
    private const double EditorHeight = 232;

    /// <summary>The Windows editor text colours (WinoMailEditor capabilities), without "Default".</summary>
    private static readonly (string Name, string Hex)[] TextColors =
    [
        ("Black", "#000000"), ("Gray", "#666666"), ("Red", "#c62828"), ("Orange", "#ef6c00"),
        ("Yellow", "#f9a825"), ("Green", "#2e7d32"), ("Blue", "#1565c0"), ("Purple", "#6a1b9a")
    ];

    private static readonly string[] Fonts = ComposePageViewController.Fonts;
    private static readonly int[] FontSizes = ComposePageViewController.FontSizes;

    private readonly NSWindow _sheet;
    private readonly NSTextField _name;
    private readonly NSButton _save;
    private readonly NSButton _cancel;
    private readonly NSPopUpButton _fontPopup;
    private readonly NSPopUpButton _sizePopup;
    private readonly WKWebView _webView;
    private readonly AppKitHtmlMailEditorSession _editor;
    private readonly Dictionary<string, FormatButton> _toggles = [];
    private readonly string _html;
    private readonly IPreferencesService? _preferences;
    private readonly Action<Exception>? _error;
    private bool _ready;
    private bool _busy;
    private bool _presented;
    private bool _disposed;

    public SignatureEditorSheet(string? accountLine, string? name, string? html, IExternalLauncher? launcher, IPreferencesService? preferences, Action<Exception>? error)
    {
        _html = html ?? string.Empty;
        _preferences = preferences;
        _error = error;

        _sheet = new NSWindow(new CGRect(0, 0, Width, 480), NSWindowStyle.Titled, NSBackingStore.Buffered, false) { Title = Translator.SignatureEditorDialog_Title };
        _sheet.ReleaseWhenClosed(false);

        // ---- Heading ----
        var heading = WinoStyle.Label(Translator.SignatureEditorDialog_Title, WinoStyle.Heading);
        var account = WinoStyle.Label(accountLine, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        account.Hidden = string.IsNullOrWhiteSpace(accountLine);
        var headingStack = WinoLayout.VStack(2, heading, account);
        headingStack.Alignment = NSLayoutAttribute.Leading;

        // ---- Name ----
        var nameCaption = WinoStyle.Label(Translator.SignatureEditorDialog_SignatureName_TitleNew, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        _name = new NSTextField
        {
            StringValue = name?.Trim() ?? string.Empty,
            PlaceholderString = Translator.SignatureEditorDialog_SignatureName_Placeholder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _name.WidthAnchor.ConstraintEqualTo(300).Active = true;
        WinoAccessibility.Label(_name, Translator.SignatureEditorDialog_SignatureName_TitleNew);
        _name.Changed += (_, _) => Refresh();
        var nameStack = WinoLayout.VStack(4, nameCaption, _name);
        nameStack.Alignment = NSLayoutAttribute.Leading;

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
        color = new FormatButton(WinoIconGlyph.TextColor, Translator.Composer_TextColor, () => ShowColorMenu(color!));
        var bullets = Toggle("bullets", WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList);
        var numbers = Toggle("numbers", WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList);
        var outdent = new FormatButton(WinoIconGlyph.TextIndentDecrease, Translator.Composer_Outdent, () => Run(EditorCommand.Outdent()));
        var indent = new FormatButton(WinoIconGlyph.TextIndentIncrease, Translator.Composer_Indent, () => Run(EditorCommand.Indent()));
        var left = Toggle("left", WinoIconGlyph.TextAlignLeft, Translator.Composer_AlignLeft, () => EditorCommand.SetAlignment(EditorTextAlignment.Left));
        var center = Toggle("center", WinoIconGlyph.TextAlignCenter, Translator.Composer_AlignCenter, () => EditorCommand.SetAlignment(EditorTextAlignment.Center));
        var right = Toggle("right", WinoIconGlyph.TextAlignRight, Translator.Composer_AlignRight, () => EditorCommand.SetAlignment(EditorTextAlignment.Right));
        var link = new FormatButton(WinoIconGlyph.Link, Translator.Composer_InsertLink, () => Observe(InsertLinkAsync()));
        var image = new FormatButton(WinoIconGlyph.Image, Translator.Composer_InsertPicture, () => Run(EditorCommand.InsertImage()));
        var clear = new FormatButton(WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, () => Run(EditorCommand.ClearFormatting()));
        left.IsActive = true;

        var toolbarItems = WinoLayout.HStack(1,
            _fontPopup, _sizePopup, Divider(),
            bold, italic, underline, strike, color, Divider(),
            bullets, numbers, outdent, indent, Divider(),
            left, center, right, Divider(),
            link, image, clear);
        toolbarItems.EdgeInsets = new NSEdgeInsets(0, 4, 0, 4);
        toolbarItems.SetCustomSpacing(4, _fontPopup);
        toolbarItems.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var toolbar = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.ControlRadius,
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.06)),
            Stroke = WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
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

        // ---- Editor (the composer's WKWebView editor session) ----
        _webView = new WKWebView(CGRect.Empty, new WKWebViewConfiguration()) { TranslatesAutoresizingMaskIntoConstraints = false };
        _webView.SetValueForKey(NSNumber.FromBoolean(false), new NSString("drawsBackground"));
        WinoAccessibility.Label(_webView, Translator.SettingsSignature_Title);
        var editorSurface = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.ControlRadius,
            Fill = WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0x1E1E1E)),
            Stroke = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.10), WinoStyle.Hex(0xFFFFFF, 0.10)),
            StrokeWidth = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(_webView, editorSurface, 6, 8, 6, 8);
        editorSurface.HeightAnchor.ConstraintEqualTo((nfloat)EditorHeight).Active = true;

        _editor = new AppKitHtmlMailEditorSession(_webView);
        if (launcher is not null) _editor.Configure(launcher);
        _editor.OperationFailed += (_, exception) => _error?.Invoke(exception);
        _editor.StateChanged += (_, state) => OnMain(() => ApplyState(state));
        _editor.ImageInsertionRequested += (_, _) => OnMain(() => Observe(PickImagesAsync()));
        _editor.ShortcutRequested += (_, kind) => { if (kind == EditorShortcutKind.OpenLinkDialog) OnMain(() => Observe(InsertLinkAsync())); };

        var editorStack = WinoLayout.VStack(8, toolbar, editorSurface);
        toolbar.WidthAnchor.ConstraintEqualTo(editorStack.WidthAnchor).Active = true;
        editorSurface.WidthAnchor.ConstraintEqualTo(editorStack.WidthAnchor).Active = true;

        // ---- Footer ----
        var hint = WinoStyle.Label(Translator.SignatureEditorDialog_ImagesHint, WinoStyle.Caption, WinoStyle.TertiaryText);
        hint.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        hint.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b", TranslatesAutoresizingMaskIntoConstraints = false };
        _save = SettingsBinder.CreateButton(Translator.Buttons_Save, primary: true);
        _save.KeyEquivalent = "\r";
        _save.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask;
        var footer = WinoLayout.HStack(WinoStyle.Space2, hint, WinoLayout.Spacer(), _cancel, _save);

        var stack = WinoLayout.VStack(14, headingStack, nameStack, editorStack, footer);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(20, 20, 20, 20);
        foreach (var view in new NSView[] { editorStack, footer })
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        stack.WidthAnchor.ConstraintEqualTo((nfloat)Width).Active = true;
        WinoLayout.Fill(stack, _sheet.ContentView!);
        _sheet.SetContentSize(stack.FittingSize);
        _sheet.InitialFirstResponder = _name;
        Refresh();
    }

    /// <summary>Shows the sheet; returns the trimmed name and the editor HTML, or null when cancelled.</summary>
    public Task<(string Name, string HtmlBody)?> PresentAsync(NSWindow parent)
    {
        if (_presented) throw new InvalidOperationException("The signature editor is presented once.");
        _presented = true;
        var completion = new TaskCompletionSource<(string Name, string HtmlBody)?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Finish((string, string)? result)
        {
            if (!completion.TrySetResult(result)) return;
            parent.EndSheet(_sheet);
        }

        _cancel.Activated += (_, _) => Finish(null);
        _save.Activated += async (_, _) =>
        {
            if (!CanSave) return;
            _busy = true;
            Refresh();
            try
            {
                var html = await _editor.GetHtmlBodyAsync() ?? string.Empty;
                Finish((_name.StringValue.Trim(), html));
            }
            catch (Exception exception)
            {
                _error?.Invoke(exception);
                _busy = false;
                Refresh();
            }
        };

        NSObject? closing = null;
        closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => Finish(null), parent);
        parent.BeginSheet(_sheet, _ =>
        {
            if (closing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); closing = null; }
            completion.TrySetResult(null);
            Observe(DisposeAsync());
        });

        var dark = parent.EffectiveAppearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]) == NSAppearance.NameDarkAqua.ToString();
        Observe(LoadAsync(dark));
        return completion.Task;
    }

    private bool CanSave => _ready && !_busy && !string.IsNullOrWhiteSpace(_name.StringValue);

    private void Refresh()
    {
        _save.Enabled = CanSave;
        _cancel.Enabled = !_busy;
    }

    private async Task LoadAsync(bool dark)
    {
        await _editor.InitializeAsync();
        await _editor.SetThemeAsync(dark);
        if (_preferences is not null) await _editor.SetDefaultTypographyAsync(_preferences.ComposerFont, _preferences.ComposerFontSize);
        await _editor.RenderHtmlAsync(_html);
        OnMain(() =>
        {
            _ready = true;
            Refresh();
        });
    }

    private async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _editor.DisposeAsync(); }
        finally { OnMain(() => _sheet.Dispose()); }
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

    private void ShowColorMenu(FormatButton anchor)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        foreach (var (name, hex) in TextColors)
        {
            var swatch = WinoStyle.FromHexString(hex) ?? NSColor.Black;
            var item = new NSMenuItem(name, (_, _) => Run(EditorCommand.SetTextColor(hex)))
            {
                Image = NSImage.ImageWithSize(new CGSize(12, 12), false, rect =>
                {
                    swatch.SetFill();
                    NSBezierPath.FromRoundedRect(rect.Inset(1, 1), 2, 2).Fill();
                    return true;
                })
            };
            menu.AddItem(item);
        }
        menu.PopUpMenu(null, new CGPoint(0, anchor.Bounds.Height + 4), anchor);
    }

    // ---- Link and image insertion (composer behaviour) ----

    private async Task InsertLinkAsync()
    {
        var url = new NSTextField(new CGRect(0, 30, 300, 24)) { PlaceholderString = Translator.Composer_LinkUrlPlaceholder };
        var text = new NSTextField(new CGRect(0, 0, 300, 24)) { PlaceholderString = Translator.Composer_LinkTextPlaceholder, StringValue = _editor.CurrentState.SelectedText ?? string.Empty };
        var accessory = new NSView(new CGRect(0, 0, 300, 54));
        accessory.AddSubview(url);
        accessory.AddSubview(text);
        var alert = new NSAlert { MessageText = Translator.Composer_InsertLink, AccessoryView = accessory };
        alert.AddButton(Translator.Composer_InsertLink);
        alert.AddButton(Translator.Buttons_Cancel);
        alert.Window.InitialFirstResponder = url;
        var response = await alert.BeginSheetAsync(_sheet);
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
