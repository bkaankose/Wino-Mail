using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Editor.WebKitGtk;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// Find and replace (Mac <c>ComposePageViewController.Find</c>): Ctrl+F (through <see cref="FindInPageTargets"/>, since it is
/// the application's search accelerator) and Ctrl+H open the bar, Escape closes it, typing searches after a short delay.
/// Also the Options commands: spell check with its language, and the editor's light or dark theme.
/// </summary>
internal sealed partial class ComposeView : IFindInPageTarget
{
    private ComposeFindBar? _findBar;
    private CancellationTokenSource? _findDebounce;
    private EditorFormatButton? _spellButton;
    private EditorFormatButton? _themeButton;
    private bool _findRunning;

    internal ComposeFindBar? FindBar => _findBar;

    partial void InitializeFind()
    {
        var bar = _findBar = new ComposeFindBar();
        ToolbarSlot.Append(bar.Root);
        bar.QueryChanged += () => ScheduleFind(EditorFindDirection.Start, 150);
        bar.FindRequested += direction => Observe(RunFindAsync(direction));
        bar.ReplaceRequested += () => Observe(ReplaceAsync());
        bar.ReplaceAllRequested += () => Observe(ReplaceAllAsync());
        bar.Closed += CloseFind;
        Bindings.Own(FindInPageTargets.Register(this));

        if (Editor is { } editor)
        {
            EventHandler changed = (_, _) => RunOnUI(RefreshFindAfterEdit);
            editor.ContentChanged += changed;
            Bindings.Own(new ActionDisposable(() => editor.ContentChanged -= changed));
        }

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        Bindings.Own(GtkSignals.ConnectReturning<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => HandleFindKey(args.Keyval, args.State), false));
        PageOverlay.AddController(keys);
        Bindings.Own(new ActionDisposable(() => PageOverlay.RemoveController(keys)));

        AddOptionsCommands();
    }

    partial void DeactivateFind()
    {
        _findDebounce?.Cancel();
        _findDebounce = null;
        if (_findBar is { } bar)
        {
            bar.Dispose();
            ToolbarSlot.Remove(bar.Root);
            _findBar = null;
        }
        _spellButton?.Dispose();
        _themeButton?.Dispose();
        _spellButton = null;
        _themeButton = null;
    }

    bool IFindInPageTarget.OwnsFocus(Gtk.Window window)
        => !IsReleased && window.GetFocus() is { } focus && (ReferenceEquals(focus, PageOverlay) || focus.IsAncestor(PageOverlay));

    void IFindInPageTarget.OpenFind() => ShowFind(false);

    internal void ShowFind(bool replace)
    {
        if (_findBar is not { } bar || IsReleased) return;
        var selected = Editor?.CurrentState.SelectedText;
        var seed = !string.IsNullOrWhiteSpace(selected) && !selected.Contains('\n') ? selected.Trim() : null;
        bar.Open(replace, seed);
        if (bar.Query.Length > 0) ScheduleFind(EditorFindDirection.Start, 0);
        else bar.SetResult(new EditorFindResult(0, 0));
    }

    internal void CloseFind()
    {
        if (_findBar is not { IsOpen: true } bar) return;
        _findDebounce?.Cancel();
        bar.Close();
        if (Editor is not { } editor || _editorDisposed) return;
        Observe(CloseFindAsync(editor));
    }

    private static async Task CloseFindAsync(WebKitGtkHtmlMailEditorSession editor)
    {
        await editor.ClearFindAsync();
        await editor.FocusEditorAsync(true);
    }

    private bool HandleFindKey(uint keyval, Gdk.ModifierType state)
    {
        var modifiers = state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask);
        if (keyval == Gdk.Constants.KEY_Escape && modifiers == 0 && _findBar is { IsOpen: true })
        {
            CloseFind();
            return true;
        }
        if (modifiers == Gdk.ModifierType.ControlMask && (keyval == Gdk.Constants.KEY_h || keyval == Gdk.Constants.KEY_H))
        {
            ShowFind(true);
            return true;
        }
        if (modifiers == Gdk.ModifierType.ControlMask && (keyval == Gdk.Constants.KEY_f || keyval == Gdk.Constants.KEY_F))
        {
            ShowFind(false);
            return true;
        }
        return false;
    }

    private void ScheduleFind(EditorFindDirection direction, int delay)
    {
        _findDebounce?.Cancel();
        var source = _findDebounce = new CancellationTokenSource();
        Observe(DebouncedFindAsync(direction, delay, source.Token));
    }

    private async Task DebouncedFindAsync(EditorFindDirection direction, int delay, CancellationToken token)
    {
        try { if (delay > 0) await Task.Delay(delay, token); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;
        await Dispatcher.ExecuteOnUIThread(() => Observe(RunFindAsync(direction)));
    }

    internal async Task RunFindAsync(EditorFindDirection direction)
    {
        if (Editor is not { } editor || _editorDisposed || _findBar is not { } bar) return;
        var query = bar.Query;
        _findRunning = true;
        EditorFindResult result;
        try
        {
            result = query.Length == 0 ? new EditorFindResult(0, 0) : await editor.FindAsync(query, bar.IgnoreCase, bar.WholeWords, direction);
            if (query.Length == 0) await editor.ClearFindAsync();
        }
        finally { _findRunning = false; }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!IsReleased && ReferenceEquals(_findBar, bar)) bar.SetResult(result);
        });
    }

    internal async Task ReplaceAsync()
    {
        if (Editor is not { } editor || _editorDisposed || _findBar is not { Query.Length: > 0 } bar) return;
        var result = await editor.ReplaceAsync(bar.Replacement);
        await Dispatcher.ExecuteOnUIThread(() => bar.SetResult(result));
    }

    internal async Task ReplaceAllAsync()
    {
        if (Editor is not { } editor || _editorDisposed || _findBar is not { Query.Length: > 0 } bar) return;
        await editor.FindAsync(bar.Query, bar.IgnoreCase, bar.WholeWords, EditorFindDirection.Start);
        await editor.ReplaceAllAsync(bar.Replacement);
        await RunFindAsync(EditorFindDirection.Start);
    }

    private void RefreshFindAfterEdit()
    {
        if (_findBar is not { IsOpen: true, Query.Length: > 0 } || _findRunning) return;
        ScheduleFind(EditorFindDirection.Start, 250);
    }

    private void AddOptionsCommands()
    {
        if (FormatToolbar is not { } toolbar) return;
        var preferences = ViewModel.PreferencesService;
        var spell = _spellButton = new EditorFormatButton(WinoIconGlyph.TextProofingTools, Translator.Composer_SpellCheck, ShowSpellMenu,
            toggle: true, tooltip: Translator.Composer_SpellCheckDescription) { IsActive = preferences.IsComposerSpellCheckEnabled };
        var theme = _themeButton = new EditorFormatButton(_dark ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, ThemeLabel(_dark), ToggleEditorTheme);
        toolbar.AddOptionsItems(
            EditorFormatToolbar.Item(spell, menu => menu.Submenu(Translator.Composer_SpellCheck, FillSpellMenu, icon: WinoIconGlyph.TextProofingTools)),
            EditorFormatToolbar.Item(theme, menu => menu.Item(ThemeLabel(_dark), ToggleEditorTheme, icon: _dark ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor)));
    }

    private static string ThemeLabel(bool dark) => dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    internal void ToggleEditorTheme() => ApplyEditorTheme(!_dark);

    partial void EditorThemeApplied() => _themeButton?.SetGlyph(_dark ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, ThemeLabel(_dark));

    private void ShowSpellMenu()
    {
        if (_spellButton is not { } button) return;
        // The toggle shows the preference, not the click: the menu decides.
        button.IsActive = ViewModel.PreferencesService.IsComposerSpellCheckEnabled;
        var menu = WinoMenus.Create();
        FillSpellMenu(menu);
        ContextMenu.Show(button.Root, menu.Build());
    }

    private void FillSpellMenu(WinoMenuBuilder menu)
    {
        var preferences = ViewModel.PreferencesService;
        var enabled = preferences.IsComposerSpellCheckEnabled;
        var current = preferences.ComposerSpellCheckLanguageCode ?? string.Empty;
        menu.Check(Translator.Composer_SpellCheck, enabled, value => SetSpellCheck(value, current), icon: WinoIconGlyph.TextProofingTools);
        menu.Separator(Translator.SettingsComposerSpellCheckLanguage_Title);
        menu.Radio("spell-language", Translator.ComposerParity_SpellCheckAutomatic, current.Length == 0, () => SetSpellCheck(true, string.Empty));
        foreach (var language in _translations.GetAvailableLanguages())
        {
            var code = language.Code;
            menu.Radio("spell-language", language.DisplayName, string.Equals(code, current, StringComparison.OrdinalIgnoreCase), () => SetSpellCheck(true, code));
        }
    }

    /// <summary>Spell check and its language are the composer preferences, like Windows and Mac.</summary>
    internal void SetSpellCheck(bool enabled, string languageCode)
    {
        var preferences = ViewModel.PreferencesService;
        preferences.IsComposerSpellCheckEnabled = enabled;
        preferences.ComposerSpellCheckLanguageCode = languageCode;
        if (_spellButton is { } button) button.IsActive = enabled;
        if (Editor is { } editor && !_editorDisposed) Observe(editor.SetSpellCheckAsync(enabled, languageCode));
    }
}
