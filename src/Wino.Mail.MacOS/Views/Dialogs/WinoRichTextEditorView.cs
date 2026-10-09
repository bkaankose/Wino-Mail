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
using Wino.Mail.MacOS.Views.Mail.Compose;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Dialogs;

/// <summary>
/// Which optional toolbar commands a <see cref="WinoRichTextEditorView"/> shows, and how it behaves.
/// Positional: new parameters are only ever appended, with defaults.
/// </summary>
/// <param name="Fused">The toolbar is joined to the top of the editor surface.</param>
/// <param name="Highlight">Highlight colour menu.</param>
/// <param name="Table">Insert table.</param>
/// <param name="Emoji">Emoji (macOS Character Viewer).</param>
/// <param name="ClearFormatting">Clear formatting.</param>
/// <param name="Tabbed">Format / Insert / Options tabs instead of one overflowing row.</param>
/// <param name="ThemeToggle">An editor light/dark button at the end of the toolbar (Windows event notes).</param>
/// <param name="SpellCheck">Spell check and spellchecking language menu.</param>
/// <param name="Paragraph">Paragraph style pop-up.</param>
/// <param name="LineSpacing">Line spacing menu.</param>
/// <param name="Alignment">Left, centre, right and justify.</param>
/// <param name="LinkTools">Remove link and image properties.</param>
/// <param name="Image">Insert picture.</param>
/// <param name="FollowsAppearance">The editor theme follows the view's effective appearance until the theme toggle overrides it.</param>
internal sealed record WinoRichTextEditorOptions(
    bool Fused = false,
    bool Highlight = false,
    bool Table = false,
    bool Emoji = false,
    bool ClearFormatting = true,
    bool Tabbed = false,
    bool ThemeToggle = false,
    bool SpellCheck = true,
    bool Paragraph = true,
    bool LineSpacing = true,
    bool Alignment = true,
    bool LinkTools = true,
    bool Image = true,
    bool FollowsAppearance = true);

/// <summary>
/// The shared <see cref="EditorFormatToolbar"/> over the composer's own WKWebView HTML editor session.
/// Used by the signature editor sheet, the email template editor page and event notes. The caller sizes
/// <see cref="EditorSurface"/>, calls <see cref="LoadAsync"/> once the view is in a window, reads HTML with
/// <see cref="GetHtmlBodyAsync"/> and disposes the session with <see cref="DisposeAsync"/>. Use on the main thread.
/// </summary>
internal sealed class WinoRichTextEditorView : NSView
{
    private readonly WinoRichTextEditorOptions _options;
    private readonly WKWebView _webView;
    private readonly AppKitHtmlMailEditorSession _editor;
    private readonly EditorFormatToolbar _toolbar;
    private readonly EditorFormatButton? _themeButton;
    private readonly IPreferencesService? _preferences;
    private readonly Action<Exception>? _error;
    private (bool Enabled, string? Language)? _spellCheck;
    private bool? _autoCorrect;
    private bool _initialized;
    private bool _themeOverridden;
    private bool _disposed;

    public WinoRichTextEditorView(WinoRichTextEditorOptions options, string accessibilityLabel, IExternalLauncher? launcher, IPreferencesService? preferences, Action<Exception>? error)
        : this(options, accessibilityLabel, launcher, preferences, null, error)
    {
    }

    /// <param name="translations">Supplies the spellchecking languages; without it the spell check menu only toggles.</param>
    public WinoRichTextEditorView(WinoRichTextEditorOptions options, string accessibilityLabel, IExternalLauncher? launcher, IPreferencesService? preferences,
        ITranslationService? translations, Action<Exception>? error)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _options = options;
        _preferences = preferences;
        _error = error;

        // ---- Editor (the composer's WKWebView editor session) ----
        _webView = new WKWebView(CGRect.Empty, new WKWebViewConfiguration()) { TranslatesAutoresizingMaskIntoConstraints = false };
        _webView.SetValueForKey(NSNumber.FromBoolean(false), new NSString("drawsBackground"));
        WinoAccessibility.Label(_webView, accessibilityLabel);
        _editor = new AppKitHtmlMailEditorSession(_webView);
        if (launcher is not null) _editor.Configure(launcher);
        _editor.OperationFailed += (_, exception) => _error?.Invoke(exception);

        // ---- Format toolbar ----
        var features = new EditorToolbarFeatures(options.Highlight, options.Table, options.Emoji, options.ClearFormatting,
            options.Paragraph, options.LineSpacing, options.Alignment, options.LinkTools, options.Image, options.SpellCheck);
        _toolbar = new EditorFormatToolbar(options.Tabbed ? EditorToolbarLayout.Tabbed : EditorToolbarLayout.SingleRow, features, preferences, translations, error);
        if (options.ThemeToggle)
        {
            _themeButton = new EditorFormatButton(WinoIconGlyph.DarkEditor, Translator.Composer_DarkTheme, ToggleTheme);
            _toolbar.AddTrailingItem(new EditorToolbarItem(_themeButton, () => new NSMenuItem(ThemeLabel(IsDarkTheme), (_, _) => ToggleTheme())));
        }
        _toolbar.Attach(_editor);

        var toolbar = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.ControlRadius,
            Fill = options.Fused ? WinoSettingsStyle.CardFill : WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.06)),
            Stroke = options.Fused ? EditorStroke : WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (options.Fused) toolbar.Corners = CACornerMask.MinXMinYCorner | CACornerMask.MaxXMinYCorner;
        toolbar.AddSubview(_toolbar);
        NSLayoutConstraint.ActivateConstraints(
        [
            toolbar.HeightAnchor.ConstraintEqualTo(36),
            _toolbar.LeadingAnchor.ConstraintEqualTo(toolbar.LeadingAnchor, options.Fused ? 6 : 4),
            _toolbar.TrailingAnchor.ConstraintEqualTo(toolbar.TrailingAnchor, -4),
            _toolbar.CenterYAnchor.ConstraintEqualTo(toolbar.CenterYAnchor)
        ]);
        toolbar.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Vertical);

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

    /// <summary>Whether the editor currently renders dark.</summary>
    public bool IsDarkTheme { get; private set; }

    /// <summary>Raised on the main thread when <see cref="IsReady"/> becomes true.</summary>
    public event EventHandler? ReadyChanged;

    /// <summary>
    /// Raised when the editor content changes. It is not an "edited by the user" signal: it also fires
    /// after <see cref="LoadAsync"/>, <see cref="SetHtmlAsync"/>, signature changes and theme changes. To
    /// know whether the user edited, compare <see cref="GetHtmlBodyAsync"/> with a baseline read after load.
    /// </summary>
    public event EventHandler? ContentChanged
    {
        add => _editor.ContentChanged += value;
        remove => _editor.ContentChanged -= value;
    }

    /// <summary>
    /// Initialises the editor session, applies the theme, the composer typography and the composer spell
    /// check, spellchecking language and autocorrect preferences (then any values given to
    /// <see cref="ConfigureSpellCheckAsync"/> / <see cref="ConfigureAutoCorrectAsync"/>), then renders <paramref name="html"/>.
    /// </summary>
    public async Task LoadAsync(string? html, bool dark)
    {
        await _editor.InitializeAsync();
        IsDarkTheme = dark;
        await _editor.SetThemeAsync(dark);
        if (_preferences is not null)
        {
            await _editor.SetDefaultTypographyAsync(_preferences.ComposerFont, _preferences.ComposerFontSize);
            await ApplySpellCheckAsync(_preferences.IsComposerSpellCheckEnabled, _preferences.ComposerSpellCheckLanguageCode);
            await _editor.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(_preferences.IsComposerAutoCorrectEnabled));
        }
        _initialized = true;
        if (_spellCheck is { } spell) await ApplySpellCheckAsync(spell.Enabled, spell.Language);
        if (_autoCorrect is { } autoCorrect) await _editor.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(autoCorrect));
        await _editor.RenderHtmlAsync(html ?? string.Empty);
        OnMain(() =>
        {
            if (_disposed) return;
            UpdateThemeButton();
            IsReady = true;
            ReadyChanged?.Invoke(this, EventArgs.Empty);
            // The appearance may have changed while the editor loaded.
            FollowAppearance();
        });
    }

    /// <summary>
    /// Replaces the document after <see cref="LoadAsync"/>. Only valid once <see cref="IsReady"/>; an earlier
    /// call is reported to the error callback and ignored.
    /// </summary>
    public async Task SetHtmlAsync(string? html)
    {
        if (_disposed) return;
        if (!IsReady)
        {
            _error?.Invoke(new InvalidOperationException("SetHtmlAsync was called before the editor finished loading."));
            return;
        }
        await _editor.RenderHtmlAsync(html ?? string.Empty);
    }

    /// <summary>The editor body. Deterministic: an unchanged document returns the same string every time.</summary>
    public Task<string?> GetHtmlBodyAsync() => _editor.GetHtmlBodyAsync();

    /// <summary>Renders the editor light or dark.</summary>
    public async Task SetThemeAsync(bool dark)
    {
        if (_disposed) return;
        IsDarkTheme = dark;
        OnMain(UpdateThemeButton);
        await _editor.SetThemeAsync(dark);
    }

    /// <summary>Moves keyboard focus into the editor.</summary>
    public Task FocusAsync() => _disposed ? Task.CompletedTask : _editor.FocusEditorAsync(true);

    /// <summary>Spell check and its language; safe before <see cref="LoadAsync"/>, which applies it after the preferences.</summary>
    public async Task ConfigureSpellCheckAsync(bool enabled, string? languageCode)
    {
        _spellCheck = (enabled, languageCode);
        if (_initialized && !_disposed) await ApplySpellCheckAsync(enabled, languageCode);
    }

    /// <summary>Autocorrect; safe before <see cref="LoadAsync"/>, which applies it after the preferences.</summary>
    public async Task ConfigureAutoCorrectAsync(bool enabled)
    {
        _autoCorrect = enabled;
        if (_initialized && !_disposed) await _editor.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(enabled));
    }

    private async Task ApplySpellCheckAsync(bool enabled, string? languageCode)
    {
        await _editor.ExecuteCommandAsync(EditorCommand.ToggleSpellCheck(enabled));
        if (!string.IsNullOrWhiteSpace(languageCode)) await _editor.ExecuteCommandAsync(EditorCommand.SetSpellCheckLanguage(languageCode));
    }

    /// <summary>A host command at the end of the toolbar; it moves into the "More" menu like the others.</summary>
    public void AddTrailingAccessory(NSView view, Func<NSMenuItem>? menuItem = null)
        => _toolbar.AddTrailingItem(new EditorToolbarItem(view, menuItem));

    /// <summary>Whether <paramref name="appearance"/> (a window's or view's effective appearance) is dark.</summary>
    public static bool IsDark(NSAppearance appearance)
        => appearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]) == NSAppearance.NameDarkAqua.ToString();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ReadyChanged = null;
        _toolbar.Detach();
        await _editor.DisposeAsync();
    }

    // ---- Theme ----

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        FollowAppearance();
    }

    /// <summary>Custom themes set the window appearance; the editor follows unless the theme toggle overrode it.</summary>
    private void FollowAppearance()
    {
        if (!_options.FollowsAppearance || _themeOverridden || !IsReady || _disposed) return;
        var dark = IsDark(EffectiveAppearance);
        if (dark != IsDarkTheme) Observe(SetThemeAsync(dark));
    }

    private void ToggleTheme()
    {
        if (_disposed) return;
        _themeOverridden = true;
        Observe(SetThemeAsync(!IsDarkTheme));
    }

    private static string ThemeLabel(bool dark) => dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private void UpdateThemeButton()
    {
        if (_themeButton is null || _disposed) return;
        _themeButton.SetGlyph(IsDarkTheme ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, ThemeLabel(IsDarkTheme));
        WinoAccessibility.Label(_themeButton.Button, ThemeLabel(IsDarkTheme));
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
}
