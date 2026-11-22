using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Editor.WebKitGtk;
using Wino.Mail.Linux.Views.Mail.Compose;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// Which optional toolbar commands a <see cref="WinoRichTextEditorView"/> shows (Mac names and defaults). Positional: new
/// parameters are only ever appended, with defaults.
/// </summary>
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
/// The composer's format toolbar and insert commands over an own HTML editor session, for dialogs and settings pages
/// (Mac <c>WinoRichTextEditorView</c>): the signature editor, the template editor page and event notes. The caller adds
/// <see cref="Root"/>, calls <see cref="LoadAsync"/> once, reads the HTML with <see cref="GetHtmlBodyAsync"/> and always
/// disposes it. Main thread.
/// </summary>
internal sealed class WinoRichTextEditorView : IAsyncDisposable
{
    private readonly WinoRichTextEditorOptions _options;
    private readonly IDispatcher _dispatcher;
    private readonly IPreferencesService? _preferences;
    private readonly Action<Exception>? _error;
    private readonly WebKitGtkHtmlMailEditorSession _editor;
    private readonly EditorFormatToolbar _toolbar;
    private readonly EditorInsertItems _insert;
    private readonly EditorFormatButton? _themeButton;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.Frame _frame = Gtk.Frame.New(null);
    private readonly IDisposable _styleSubscription;
    private (bool Enabled, string? Language)? _spellCheck;
    private string _renderedHtml = string.Empty;
    private bool _initialized;
    private bool _themeOverridden;
    private bool _disposed;

    public WinoRichTextEditorView(WinoRichTextEditorOptions options, string accessibleLabel, IDispatcher dispatcher, IExternalLauncher? launcher,
        IPreferencesService? preferences, ITranslationService? translations, Action<Exception>? error)
    {
        WinoLiveObjects.Track(this);
        _options = options;
        _dispatcher = dispatcher;
        _preferences = preferences;
        _error = error;

        _editor = new WebKitGtkHtmlMailEditorSession(dispatcher);
        if (launcher is not null) _editor.Configure(launcher);
        _editor.OperationFailed += OperationFailed;
        _editor.ContentChanged += EditorContentChanged;
        _editor.Restarted += EditorRestarted;
        var view = _editor.View;
        view.SetVexpand(true);
        view.SetHexpand(true);
        view.SetBackgroundColor(new Gdk.RGBA { Red = 0, Green = 0, Blue = 0, Alpha = 0 });
        WinoA11y.Label(view, accessibleLabel);

        var features = new EditorToolbarFeatures(options.Highlight, options.Table, options.Emoji, options.ClearFormatting,
            options.Paragraph, options.LineSpacing, options.Alignment, options.LinkTools, options.Image, options.SpellCheck);
        _toolbar = new EditorFormatToolbar(options.Tabbed ? EditorToolbarLayout.Tabbed : EditorToolbarLayout.SingleRow, features, preferences, Report);
        _insert = new EditorInsertItems(_toolbar, features, () => _root.GetRoot() as Gtk.Window, Report);
        if (options.ThemeToggle)
        {
            var theme = _themeButton = new EditorFormatButton(WinoIconGlyph.DarkEditor, ThemeLabel(false), ToggleTheme);
            _toolbar.AddTrailingItem(new EditorToolbarItem(theme.Root, menu => menu.Item(ThemeLabel(IsDarkTheme), ToggleTheme, icon: IsDarkTheme ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor)));
        }
        _toolbar.Attach(_editor);

        _frame.AddCssClass("view");
        _frame.AddCssClass("wino-rich-editor");
        if (!options.Fused) _frame.AddCssClass("card");
        _frame.SetChild(view);
        _frame.SetVexpand(true);
        _frame.SetSizeRequest(-1, 160);
        _root.Append(_toolbar.Root);
        _root.Append(_frame);
        _root.SetVexpand(true);

        var style = Adw.StyleManager.GetDefault();
        IsDarkTheme = style.Dark;
        _styleSubscription = GtkSignals.OnNotify(style, "dark", FollowAppearance);
    }

    public Gtk.Widget Root => _root;
    public WebKit.WebView EditorView => _editor.View;

    /// <summary>True once <see cref="LoadAsync"/> rendered the initial HTML.</summary>
    public bool IsReady { get; private set; }
    public bool IsDarkTheme { get; private set; }

    /// <summary>Raised on the main thread when <see cref="IsReady"/> becomes true.</summary>
    public event EventHandler? ReadyChanged;

    /// <summary>Not an "edited by the user" signal: it also follows loads, <see cref="SetHtmlAsync"/> and theme changes.</summary>
    public event EventHandler? ContentChanged;

    public async Task LoadAsync(string? html, bool dark)
    {
        if (_disposed) return;
        await _editor.SetLinkLabelsAsync(Translator.Composer_OpenLink, Translator.Composer_RemoveLink);
        await _editor.InitializeAsync();
        if (_disposed) return;
        IsDarkTheme = dark;
        await _editor.SetThemeAsync(dark);
        if (_preferences is not null)
        {
            await _editor.SetDefaultTypographyAsync(_preferences.ComposerFont, _preferences.ComposerFontSize);
            await _editor.SetSpellCheckAsync(_options.SpellCheck && _preferences.IsComposerSpellCheckEnabled, _preferences.ComposerSpellCheckLanguageCode);
            await _editor.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(_preferences.IsComposerAutoCorrectEnabled));
        }
        _initialized = true;
        if (_spellCheck is { } spell) await _editor.SetSpellCheckAsync(spell.Enabled, spell.Language);
        _renderedHtml = html ?? string.Empty;
        await _editor.RenderHtmlAsync(_renderedHtml);
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_disposed) return;
            UpdateThemeButton();
            IsReady = true;
            ReadyChanged?.Invoke(this, EventArgs.Empty);
            FollowAppearance();
        });
    }

    public async Task SetHtmlAsync(string? html)
    {
        if (_disposed) return;
        if (!IsReady)
        {
            Report(new InvalidOperationException("SetHtmlAsync was called before the editor finished loading."));
            return;
        }
        _renderedHtml = html ?? string.Empty;
        await _editor.RenderHtmlAsync(_renderedHtml);
    }

    /// <summary>The live editor body; the last loaded HTML while the web process restarts; null once disposed.</summary>
    public async Task<string?> GetHtmlBodyAsync()
    {
        if (_disposed) return null;
        try { return await _editor.GetHtmlBodyAsync(); }
        catch (InvalidOperationException) when (_editor.IsContentLost) { return _renderedHtml; }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>An explicit theme from the host wins over <see cref="WinoRichTextEditorOptions.FollowsAppearance"/> from then on.</summary>
    public Task SetThemeAsync(bool dark)
    {
        _themeOverridden = true;
        return ApplyThemeAsync(dark);
    }

    public Task FocusAsync() => _disposed ? Task.CompletedTask : _editor.FocusEditorAsync(true);

    /// <summary>Safe before <see cref="LoadAsync"/>, which applies it after the preferences.</summary>
    public async Task ConfigureSpellCheckAsync(bool enabled, string? languageCode)
    {
        _spellCheck = (enabled, languageCode);
        if (_initialized && !_disposed) await _editor.SetSpellCheckAsync(enabled, languageCode);
    }

    /// <summary>A host command at the end of the toolbar; it moves into the More menu like the others.</summary>
    public void AddTrailingAccessory(Gtk.Widget widget, Action<WinoMenuBuilder>? menuItem = null)
        => _toolbar.AddTrailingItem(new EditorToolbarItem(widget, menuItem));

#if DEBUG
    internal WebKitGtkHtmlMailEditorSession SessionForDebug => _editor;
    internal EditorFormatToolbar ToolbarForDebug => _toolbar;
#endif

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ReadyChanged = null;
        ContentChanged = null;
        _styleSubscription.Dispose();
        _editor.OperationFailed -= OperationFailed;
        _editor.ContentChanged -= EditorContentChanged;
        _editor.Restarted -= EditorRestarted;
        _insert.Dispose();
        _toolbar.Detach();
        _toolbar.Dispose();
        _themeButton?.Dispose();
        try { await _editor.DisposeAsync(); }
        catch (Exception exception) { _error?.Invoke(exception); }
        await _dispatcher.ExecuteOnUIThread(() => _frame.SetChild(null));
    }

    private async Task ApplyThemeAsync(bool dark)
    {
        if (_disposed) return;
        IsDarkTheme = dark;
        UpdateThemeButton();
        await _editor.SetThemeAsync(dark);
    }

    private void FollowAppearance()
    {
        if (!_options.FollowsAppearance || _themeOverridden || !IsReady || _disposed) return;
        var dark = Adw.StyleManager.GetDefault().Dark;
        if (dark != IsDarkTheme) Observe(ApplyThemeAsync(dark));
    }

    private void ToggleTheme()
    {
        if (_disposed) return;
        _themeOverridden = true;
        Observe(ApplyThemeAsync(!IsDarkTheme));
    }

    private static string ThemeLabel(bool dark) => dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private void UpdateThemeButton() => _themeButton?.SetGlyph(IsDarkTheme ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, ThemeLabel(IsDarkTheme));

    private void EditorContentChanged(object? sender, EventArgs args) => ContentChanged?.Invoke(this, EventArgs.Empty);

    private void OperationFailed(object? sender, Exception exception) => Report(exception);

    private async void EditorRestarted(object? sender, EventArgs args)
    {
        if (_disposed) return;
        try { await _editor.RenderHtmlAsync(_renderedHtml); }
        catch (Exception exception) { Report(exception); }
    }

    private void Report(Exception exception)
    {
        if (!_disposed) _error?.Invoke(exception);
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Report(exception); }
    }
}
