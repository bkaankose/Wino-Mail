using Wino.Mail.UIKit.Views.Settings;
using CoreGraphics;
using UIKit;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Editor.WebKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Dialogs;

/// <summary>
/// A small rich text editor: a formatting toolbar (bold, italic, underline, bullet and numbered lists, link, clear
/// formatting) over the composer's own WKWebView editor session (<see cref="WebKitHtmlMailEditorSession"/>, shared
/// with the Mac). The UIKit twin of the Mac WinoRichTextEditorView, used by the signature editor sheet. The caller
/// sizes <see cref="EditorSurface"/>, calls <see cref="LoadAsync"/> once the view is in a window, reads HTML with
/// <see cref="GetHtmlBodyAsync"/> and disposes the session with <see cref="DisposeAsync"/>. Use on the main thread.
/// </summary>
internal sealed class WinoRichTextEditorView : UIView
{
    private readonly WKWebView _webView;
    private readonly WebKitHtmlMailEditorSession _editor;
    private readonly IPreferencesService? _preferences;
    private readonly Action<Exception>? _error;
    private readonly List<(UIButton Button, Func<EditorState, bool> IsOn)> _toggles = [];
    private EditorState _state = new();
    private bool _disposed;

    public WinoRichTextEditorView(string accessibilityLabel, IExternalLauncher? launcher, IPreferencesService? preferences, Action<Exception>? error)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _preferences = preferences;
        _error = error;

        // ---- Editor (the composer's WKWebView editor session) ----
        _webView = new WKWebView(CGRect.Empty, new WKWebViewConfiguration())
        {
            Opaque = false,
            BackgroundColor = UIColor.Clear,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _webView.ScrollView.BackgroundColor = UIColor.Clear;
        WinoAccessibility.Label(_webView, accessibilityLabel);
        _editor = new WebKitHtmlMailEditorSession(_webView);
        if (launcher is not null) _editor.Configure(launcher);
        _editor.OperationFailed += (_, exception) => _error?.Invoke(exception);
        _editor.StateChanged += (_, state) => OnMain(() => ApplyState(state));
        _editor.ShortcutRequested += (_, kind) => { if (kind == EditorShortcutKind.OpenLinkDialog) OnMain(ShowLinkDialog); };

        // ---- Format toolbar ----
        var buttons = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Spacing = 2,
            Alignment = UIStackViewAlignment.Center,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        buttons.AddArrangedSubview(Toggle(WinoIconGlyph.TextBold, Translator.Composer_Bold, EditorCommand.ToggleBold, state => state.IsBold));
        buttons.AddArrangedSubview(Toggle(WinoIconGlyph.TextItalic, Translator.Composer_Italic, EditorCommand.ToggleItalic, state => state.IsItalic));
        buttons.AddArrangedSubview(Toggle(WinoIconGlyph.TextUnderline, Translator.Composer_Underline, EditorCommand.ToggleUnderline, state => state.IsUnderline));
        buttons.AddArrangedSubview(Divider());
        buttons.AddArrangedSubview(Toggle(WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList, state => state.IsUnorderedList));
        buttons.AddArrangedSubview(Toggle(WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList, state => state.IsOrderedList));
        buttons.AddArrangedSubview(Divider());
        buttons.AddArrangedSubview(Button(WinoIconGlyph.Link, Translator.Composer_InsertLink, ShowLinkDialog));
        buttons.AddArrangedSubview(Button(WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, () => Run(EditorCommand.ClearFormatting())));

        // The row scrolls sideways when the sheet is narrower than the toolbar (iPhone with large text).
        var scroller = new UIScrollView { ShowsHorizontalScrollIndicator = false, AlwaysBounceHorizontal = false, TranslatesAutoresizingMaskIntoConstraints = false };
        scroller.AddSubview(buttons);
        NSLayoutConstraint.ActivateConstraints(
        [
            buttons.LeadingAnchor.ConstraintEqualTo(scroller.ContentLayoutGuide.LeadingAnchor, 4),
            buttons.TrailingAnchor.ConstraintEqualTo(scroller.ContentLayoutGuide.TrailingAnchor, -4),
            buttons.TopAnchor.ConstraintEqualTo(scroller.ContentLayoutGuide.TopAnchor),
            buttons.BottomAnchor.ConstraintEqualTo(scroller.ContentLayoutGuide.BottomAnchor),
            buttons.HeightAnchor.ConstraintEqualTo(scroller.FrameLayoutGuide.HeightAnchor),
            scroller.HeightAnchor.ConstraintEqualTo((nfloat)WinoStyle.MinimumHitTarget)
        ]);
        var toolbar = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.ControlRadius,
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.06)),
            Stroke = EditorStroke,
            StrokeWidth = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(scroller, toolbar);

        EditorSurface = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.ControlRadius,
            Fill = WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x1E1E1E)),
            Stroke = EditorStroke,
            StrokeWidth = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(_webView, EditorSurface, 6, 8, 6, 8);

        AddSubview(toolbar);
        AddSubview(EditorSurface);
        NSLayoutConstraint.ActivateConstraints(
        [
            toolbar.TopAnchor.ConstraintEqualTo(TopAnchor),
            toolbar.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            toolbar.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            EditorSurface.TopAnchor.ConstraintEqualTo(toolbar.BottomAnchor, 8),
            EditorSurface.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            EditorSurface.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            EditorSurface.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);

        // The editor follows light and dark (custom themes set the window's interface style).
        WinoTraits.OnAppearanceChanged(this, view => view.FollowAppearance());
    }

    private static UIColor EditorStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.10), WinoStyle.Hex(0xFFFFFF, 0.10));

    /// <summary>The rounded editor surface below the toolbar; the caller sets or constrains its height.</summary>
    public WinoSurfaceView EditorSurface { get; }

    /// <summary>True once <see cref="LoadAsync"/> rendered the initial HTML.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Whether the editor currently renders dark.</summary>
    public bool IsDarkTheme { get; private set; }

    /// <summary>Raised on the main thread when <see cref="IsReady"/> becomes true.</summary>
    public event EventHandler? ReadyChanged;

    /// <summary>Raised when the editor content changes (also after <see cref="LoadAsync"/> and theme changes).</summary>
    public event EventHandler? ContentChanged
    {
        add => _editor.ContentChanged += value;
        remove => _editor.ContentChanged -= value;
    }

    /// <summary>
    /// Initialises the editor session, applies the theme and the composer typography, spell check, spellchecking
    /// language and autocorrect preferences, then renders <paramref name="html"/>.
    /// </summary>
    public async Task LoadAsync(string? html, bool dark)
    {
        await _editor.InitializeAsync();
        IsDarkTheme = dark;
        await _editor.SetThemeAsync(dark);
        if (_preferences is not null)
        {
            await _editor.SetDefaultTypographyAsync(_preferences.ComposerFont, _preferences.ComposerFontSize);
            await _editor.ExecuteCommandAsync(EditorCommand.ToggleSpellCheck(_preferences.IsComposerSpellCheckEnabled));
            if (!string.IsNullOrWhiteSpace(_preferences.ComposerSpellCheckLanguageCode))
                await _editor.ExecuteCommandAsync(EditorCommand.SetSpellCheckLanguage(_preferences.ComposerSpellCheckLanguageCode));
            await _editor.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(_preferences.IsComposerAutoCorrectEnabled));
        }
        await _editor.RenderHtmlAsync(html ?? string.Empty);
        OnMain(() =>
        {
            if (_disposed) return;
            IsReady = true;
            ReadyChanged?.Invoke(this, EventArgs.Empty);
            // The appearance may have changed while the editor loaded.
            FollowAppearance();
        });
    }

    /// <summary>The editor body. Deterministic: an unchanged document returns the same string every time.</summary>
    public Task<string?> GetHtmlBodyAsync() => _editor.GetHtmlBodyAsync();

    /// <summary>Moves keyboard focus into the editor.</summary>
    public Task FocusAsync() => _disposed ? Task.CompletedTask : _editor.FocusEditorAsync(true);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ReadyChanged = null;
        await _editor.DisposeAsync();
    }

    // ---- Toolbar ----

    private UIButton Toggle(WinoIconGlyph glyph, string label, Func<EditorCommand> command, Func<EditorState, bool> isOn)
    {
        var button = Button(glyph, label, () => Run(command()));
        _toggles.Add((button, isOn));
        return button;
    }

    private static UIButton Button(WinoIconGlyph glyph, string label, Action action)
    {
        var button = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 18, WinoStyle.PrimaryText, label);
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        button.Configuration = configuration;
        WinoLayout.Size(button, WinoStyle.MinimumHitTarget, WinoStyle.MinimumHitTarget);
        WinoAccessibility.Label(button, label);
        button.PointerInteractionEnabled = true;
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    private static UIView Divider()
    {
        var divider = new UIView { BackgroundColor = WinoStyle.Separator, TranslatesAutoresizingMaskIntoConstraints = false, IsAccessibilityElement = false };
        WinoLayout.Size(divider, 1, 20);
        return divider;
    }

    private void ApplyState(EditorState state)
    {
        if (_disposed) return;
        _state = state;
        foreach (var (button, isOn) in _toggles)
        {
            var on = isOn(state);
            if (button.Configuration is { } configuration)
            {
                // Configurations and their backgrounds are copies: change the copy, then assign it back.
                var background = configuration.Background;
                background.BackgroundColor = on ? WinoStyle.SelectedFill : UIColor.Clear;
                background.CornerRadius = (nfloat)WinoStyle.ControlRadius;
                configuration.Background = background;
                button.Configuration = configuration;
            }
            button.AccessibilityTraits = on ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        }
    }

    private void Run(EditorCommand command)
    {
        if (_disposed || !IsReady) return;
        Observe(_editor.ExecuteCommandAsync(command));
    }

    /// <summary>
    /// Insert link: an alert with URL and text (prefilled from the selection), plus Remove link when the selection is
    /// already a link. The editor restores its own selection before it applies the command.
    /// </summary>
    private void ShowLinkDialog()
    {
        if (_disposed || !IsReady || Presenter() is not { } presenter) return;
        var existing = _state.LinkUrl;
        var alert = UIAlertController.Create(Translator.Composer_InsertLink, null, UIAlertControllerStyle.Alert);
        alert.AddTextField(field =>
        {
            field.Placeholder = Translator.Composer_LinkUrlPlaceholder;
            field.Text = existing ?? string.Empty;
            field.KeyboardType = UIKeyboardType.Url;
            field.AutocapitalizationType = UITextAutocapitalizationType.None;
            field.AutocorrectionType = UITextAutocorrectionType.No;
            WinoAccessibility.Label(field, Translator.Composer_LinkUrl);
        });
        alert.AddTextField(field =>
        {
            field.Placeholder = Translator.Composer_LinkTextPlaceholder;
            field.Text = _state.SelectedText ?? string.Empty;
            WinoAccessibility.Label(field, Translator.Composer_LinkText);
        });
        alert.AddAction(UIAlertAction.Create(Translator.Buttons_Insert, UIAlertActionStyle.Default, _ =>
        {
            var fields = alert.TextFields ?? [];
            var url = fields.Length > 0 ? (fields[0].Text ?? string.Empty).Trim() : string.Empty;
            var text = fields.Length > 1 ? fields[1].Text?.Trim() : null;
            if (url.Length == 0) return;
            Run(EditorCommand.InsertLink(new EditorLinkCommandArgs(url, string.IsNullOrEmpty(text) ? null : text)));
        }));
        if (!string.IsNullOrEmpty(existing))
            alert.AddAction(UIAlertAction.Create(Translator.Composer_RemoveLink, UIAlertActionStyle.Destructive, _ => Run(EditorCommand.RemoveLink())));
        alert.AddAction(UIAlertAction.Create(Translator.Buttons_Cancel, UIAlertActionStyle.Cancel, null));
        if (alert.Actions is { Length: > 0 } actions) alert.PreferredAction = actions[0];
        presenter.PresentViewController(alert, true, null);
    }

    /// <summary>The view controller that owns this view (the sheet page), for the link alert.</summary>
    private UIViewController? Presenter()
    {
        for (UIResponder? responder = this; responder is not null; responder = responder.NextResponder)
            if (responder is UIViewController controller) return controller;
        return null;
    }

    // ---- Theme ----

    private void FollowAppearance()
    {
        if (!IsReady || _disposed) return;
        var dark = WinoTraits.IsDark(TraitCollection);
        if (dark == IsDarkTheme) return;
        IsDarkTheme = dark;
        Observe(_editor.SetThemeAsync(dark));
    }

    // ---- Helpers ----

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _error?.Invoke(exception); }
    }

    private void OnMain(Action action) => BeginInvokeOnMainThread(action);
}
