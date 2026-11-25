using MimeKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Editor;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal sealed partial class ComposeView
{
    private static readonly TimeSpan RenderEchoWindow = TimeSpan.FromMilliseconds(600);
    private bool _dark;
    private long _renderedAt;
    private int _executingEditorShortcut;

    internal bool IsEditorDark => _dark;

    private void BindEditorTheme()
    {
        var style = Adw.StyleManager.GetDefault();
        _dark = style.Dark;
        Bindings.Own(GtkSignals.OnNotify(style, "dark", () =>
        {
            var dark = style.Dark;
            if (dark == _dark || IsReleased) return;
            ApplyEditorTheme(dark);
        }));
        EventHandler changed = (_, _) => RunOnUI(() => Observe(ApplySendShortcutAsync()));
        _shortcuts.KeyboardShortcutsChanged += changed;
        Bindings.Own(new ActionDisposable(() => _shortcuts.KeyboardShortcutsChanged -= changed));
    }

    partial void EditorThemeApplied();

    internal void ApplyEditorTheme(bool dark)
    {
        _dark = dark;
        EditorThemeApplied();
        if (Editor is { } editor && !_editorDisposed) Observe(editor.SetThemeAsync(dark));
    }

    private async Task<string> GetHtmlBodyAsync()
    {
        if (Editor is not { } editor || _editorDisposed) return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty;
        try { return await editor.GetHtmlBodyAsync() ?? string.Empty; }
        catch (ObjectDisposedException) { return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty; }
        catch (OperationCanceledException) { return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty; }
        catch (InvalidOperationException) when (editor.IsContentLost) { return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty; }
    }

    private async void EditorRestarted(object? sender, EventArgs e)
    {
        try { await RenderBodyAsync(ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty); }
        catch (Exception exception) { if (!IsReleased) ReportError(exception); }
    }

    private async Task RenderBodyAsync(string html)
    {
        if (Editor is not { } editor || _editorDisposed) return;
        var preferences = ViewModel.PreferencesService;
        await editor.SetRemoteContentPolicyAsync(ViewModel.ShouldBlockRemoteContent ? RemoteContentPolicy.Blocked : RemoteContentPolicy.ImagesAndFontsAllowed);
        await editor.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
        await editor.SetSpellCheckAsync(preferences.IsComposerSpellCheckEnabled, preferences.ComposerSpellCheckLanguageCode);
        await editor.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(preferences.IsComposerAutoCorrectEnabled));
        await editor.RenderHtmlAsync(html ?? string.Empty);
        _renderedAt = Environment.TickCount64;
    }

    // setContent posts contentChanged after its debounce; that echo of a render is not an edit and must not re-save
    // (and re-upload) a draft that was only opened.
    private void EditorContentChanged()
    {
        if (Environment.TickCount64 - _renderedAt < (long)RenderEchoWindow.TotalMilliseconds) return;
        ScheduleAutosave();
    }

    private bool ShouldFocusEditor()
    {
        var inReplyTo = ViewModel.CurrentMimeMessage?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo)) inReplyTo = ViewModel.CurrentMailDraftItem?.MailCopy?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo) && ViewModel.CurrentMimeMessage is { } mime && mime.Headers.Contains(HeaderId.InReplyTo))
            inReplyTo = mime.Headers[HeaderId.InReplyTo];
        return !string.IsNullOrWhiteSpace(inReplyTo);
    }

    private async Task ApplyInitialFocusAsync(bool requested)
    {
        if (Editor is not { } editor || _editorDisposed) return;
        var focusEditor = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            if (requested && ShouldFocusEditor()) focusEditor = true;
            else if (requested || ViewModel.ToItems.Count == 0) FocusRecipients();
        });
        if (focusEditor) await editor.FocusEditorAsync(true);
    }

    // The configured Send shortcut reaches the page from inside the web view through the editor bridge (Windows).
    private async Task ApplySendShortcutAsync()
    {
        if (Editor is not { } editor || _editorDisposed) return;
        var gestures = _shortcuts.EnabledShortcutsSnapshot
            .Where(static shortcut => shortcut.Mode == WinoApplicationMode.Mail && shortcut.Action == KeyboardShortcutAction.Send)
            .Where(static shortcut => !shortcut.ModifierKeys.HasFlag(ModifierKeys.Command))
            .Select(static shortcut => new EditorApplicationShortcutGesture(shortcut.Key,
                shortcut.ModifierKeys.HasFlag(ModifierKeys.Control),
                shortcut.ModifierKeys.HasFlag(ModifierKeys.Alt),
                shortcut.ModifierKeys.HasFlag(ModifierKeys.Shift)))
            .ToArray();
        await editor.SetApplicationShortcutsAsync(gestures);
        Serilog.Log.Debug("Composer send gestures: {Gestures}", string.Join(", ", gestures.Select(static g => $"{(g.Control ? "Ctrl+" : "")}{(g.Alt ? "Alt+" : "")}{(g.Shift ? "Shift+" : "")}{g.Key}")));
    }

    private async void EditorApplicationShortcutRequested(object? sender, EditorApplicationShortcutGesture gesture)
    {
        if (Interlocked.Exchange(ref _executingEditorShortcut, 1) != 0) return;
        var modifiers = ModifierKeys.None;
        if (gesture.Control) modifiers |= ModifierKeys.Control;
        if (gesture.Alt) modifiers |= ModifierKeys.Alt;
        if (gesture.Shift) modifiers |= ModifierKeys.Shift;
        var shortcut = _shortcuts.EnabledShortcutsSnapshot.FirstOrDefault(item =>
            item.Mode == WinoApplicationMode.Mail && item.Action == KeyboardShortcutAction.Send && item.ModifierKeys == modifiers
            && string.Equals(item.Key, gesture.Key, StringComparison.OrdinalIgnoreCase));
        try
        {
            if (shortcut is null || IsReleased) return;
            await SyncAllRecipientsAsync();
            await ViewModel.KeyboardShortcutHook(new KeyboardShortcutTriggerDetails
            {
                ShortcutId = shortcut.Id,
                Mode = shortcut.Mode,
                Action = shortcut.Action,
                Key = shortcut.Key,
                ModifierKeys = shortcut.ModifierKeys,
                InputContext = IsPoppedOut ? KeyboardShortcutInputContext.PopOutCompose : KeyboardShortcutInputContext.Compose,
                Sender = this,
                Origin = this
            });
        }
        catch (Exception exception) { if (!IsReleased) ReportError(exception); }
        finally { Volatile.Write(ref _executingEditorShortcut, 0); }
    }
}
