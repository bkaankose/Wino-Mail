using AppKit;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Editor.AppKit;
using Wino.Mail.MacOS.Views.Mail.Compose;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Find in message (design board ComposeFindBar), the composer's keyboard shortcuts and input-method
/// safety. Cmd+F finds, Cmd+Option+F finds and replaces, Cmd+G / Shift+Cmd+G step, Cmd+E uses the
/// selection, Esc closes the bar (or cancels a running rewrite). Shortcuts only apply while focus is
/// inside this composer and never while an input method has marked (uncommitted) text.
/// </summary>
public sealed partial class ComposePageViewController
{
    private const ushort EscapeKeyCode = 53;
    private ComposeFindBar _findBar = null!;
    private NSObject? _keyMonitor;
    private CancellationTokenSource? _findDebounce;
    private bool _findRunning;

    private NSView BuildFindBar()
    {
        _findBar = new ComposeFindBar { Hidden = true };
        _findBar.QueryChanged += (_, _) => ScheduleFind(EditorFindDirection.Start, 120);
        _findBar.NextRequested += (_, _) => Observe(RunFindAsync(EditorFindDirection.Next));
        _findBar.PreviousRequested += (_, _) => Observe(RunFindAsync(EditorFindDirection.Previous));
        _findBar.ReplaceRequested += (_, _) => Observe(ReplaceAsync());
        _findBar.ReplaceAllRequested += (_, _) => Observe(ReplaceAllAsync());
        _findBar.CloseRequested += (_, _) => CloseFind();
        return _findBar;
    }

    internal bool IsFindVisible => _findBar is { Hidden: false };

    private void ShowFind(bool replace, string? seed = null)
    {
        if (_findBar is null) return;
        if (seed is not null) _findBar.SetQuery(seed);
        _findBar.SetReplaceVisible(replace || (_findBar.IsReplaceVisible && !_findBar.Hidden));
        if (_findBar.Hidden)
        {
            NSAnimationContext.RunAnimation(context =>
            {
                context.Duration = NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion ? 0 : 0.15;
                context.AllowsImplicitAnimation = true;
                _findBar.Hidden = false;
                View.LayoutSubtreeIfNeeded();
            });
        }
        _findBar.FocusSearch();
        if (_findBar.Query.Length > 0) ScheduleFind(EditorFindDirection.Start, 0);
        else _findBar.SetResult(0, 0);
    }

    private void CloseFind()
    {
        if (_findBar is null || _findBar.Hidden) return;
        _findDebounce?.Cancel();
        NSAnimationContext.RunAnimation(context =>
        {
            context.Duration = NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion ? 0 : 0.15;
            context.AllowsImplicitAnimation = true;
            _findBar.Hidden = true;
            View.LayoutSubtreeIfNeeded();
        });
        if (_editor is null || _editorDisposed) return;
        // Leaves the caret on the current match, in the editor.
        Observe(CloseFindAsync());
    }

    private async Task CloseFindAsync()
    {
        await _editor!.ClearFindAsync();
        await _editor.FocusEditorAsync(true);
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

    private async Task RunFindAsync(EditorFindDirection direction)
    {
        if (_editor is null || _editorDisposed || _findBar is null) return;
        var query = _findBar.Query;
        if (query.Length == 0 && direction != EditorFindDirection.Start)
        {
            ShowFind(false);
            return;
        }
        _findRunning = true;
        EditorFindResult result;
        try { result = await _editor.FindAsync(query, _findBar.IgnoreCase, _findBar.WholeWords, direction); }
        finally { _findRunning = false; }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed) return;
            _findBar.SetResult(result.Index, result.Count);
            if (query.Length > 0) Announce(_findBar.CounterText);
        });
    }

    private async Task ReplaceAsync()
    {
        if (_editor is null || _editorDisposed || _findBar.Query.Length == 0) return;
        var result = await _editor.ReplaceAsync(_findBar.Replacement);
        await Dispatcher.ExecuteOnUIThread(() => _findBar.SetResult(result.Index, result.Count));
    }

    private async Task ReplaceAllAsync()
    {
        if (_editor is null || _editorDisposed || _findBar.Query.Length == 0) return;
        await _editor.FindAsync(_findBar.Query, _findBar.IgnoreCase, _findBar.WholeWords, EditorFindDirection.Start);
        await _editor.ReplaceAllAsync(_findBar.Replacement);
        await RunFindAsync(EditorFindDirection.Start);
    }

    /// <summary>Edits move matches; the counter follows while the bar is open.</summary>
    private void RefreshFindAfterEdit()
    {
        if (!IsFindVisible || _findRunning || _findBar.Query.Length == 0) return;
        ScheduleFind(EditorFindDirection.Start, 250);
    }

    private void UseSelectionForFind()
    {
        var selection = _editor?.CurrentState.SelectedText;
        if (Window()?.FirstResponder is NSTextView { FieldEditor: true } fieldEditor && fieldEditor.SelectedRange.Length > 0)
            selection = fieldEditor.Value?.Substring((int)fieldEditor.SelectedRange.Location, (int)fieldEditor.SelectedRange.Length);
        if (string.IsNullOrWhiteSpace(selection)) return;
        _findBar.SetQuery(selection.Trim());
        if (IsFindVisible) ScheduleFind(EditorFindDirection.Start, 0);
    }

    // ---- Keyboard ----

    private void InstallKeyMonitor()
    {
        if (_keyMonitor is not null) return;
        _keyMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, HandleKeyDown);
    }

    private void RemoveKeyMonitor()
    {
        if (_keyMonitor is null) return;
        NSEvent.RemoveMonitor(_keyMonitor);
        _keyMonitor = null;
        _findDebounce?.Cancel();
    }

    private NSEvent? HandleKeyDown(NSEvent theEvent)
    {
        if (Bindings.IsDisposed || !ViewLoaded || View.Window is not { } window || theEvent.Window != window) return theEvent;
        var responder = window.FirstResponder;
        if (!IsInComposer(responder)) return theEvent;
        // An input method owns every key while it composes (Japanese, Chinese, Korean...).
        if (HasMarkedText(responder)) return theEvent;

        var flags = theEvent.ModifierFlags & (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask | NSEventModifierMask.ShiftKeyMask | NSEventModifierMask.ControlKeyMask);
        // The recipient suggestion popup owns the arrow keys, Return and Escape while it is open.
        if (HandleSuggestionKey(theEvent, flags, responder)) return null;
        if (theEvent.KeyCode == EscapeKeyCode && flags == 0)
        {
            if (ViewModel.RewriteSession.IsBusy)
            {
                ViewModel.RewriteSession.CancelCommand.Execute(null);
                return null;
            }
            if (IsFindVisible)
            {
                CloseFind();
                return null;
            }
            return theEvent;
        }

        var key = theEvent.CharactersIgnoringModifiers?.ToLowerInvariant();
        if (TryHandleFormattingKey(key, flags, responder)) return null;
        if (MatchesSendFallback(theEvent, flags))
        {
            Observe(SendAsync());
            return null;
        }
        switch (key)
        {
            case "f" when flags == NSEventModifierMask.CommandKeyMask:
                ShowFind(false, SelectionSeed());
                return null;
            case "f" when flags == (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask):
                ShowFind(true, SelectionSeed());
                return null;
            case "g" when flags == NSEventModifierMask.CommandKeyMask:
                Observe(RunFindAsync(EditorFindDirection.Next));
                return null;
            case "g" when flags == (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask):
                Observe(RunFindAsync(EditorFindDirection.Previous));
                return null;
            case "e" when flags == NSEventModifierMask.CommandKeyMask:
                UseSelectionForFind();
                return null;
        }
        return theEvent;
    }

    /// <summary>A short single-line editor selection seeds a newly opened bar, like other Mac apps.</summary>
    private string? SelectionSeed()
    {
        if (IsFindVisible || !IsInEditor(Window()?.FirstResponder)) return null;
        var selected = _editor?.CurrentState.SelectedText;
        return string.IsNullOrWhiteSpace(selected) || selected.Length > 100 || selected.Contains('\n') ? null : selected.Trim();
    }

    private bool IsInComposer(NSResponder? responder)
        => responder is NSView view && ViewLoaded && (view == View || view.IsDescendantOf(View));

    private bool IsInEditor(NSResponder? responder)
        => responder is NSView view && _webView is not null && (view == _webView || view.IsDescendantOf(_webView));

    private static readonly Selector HasMarkedTextSelector = new("hasMarkedText");

    /// <summary>True while an input method holds uncommitted (marked) text in <paramref name="responder"/>.</summary>
    internal static bool HasMarkedText(NSResponder? responder)
    {
        if (responder is NSTextView textView) return textView.HasMarkedText;
        if (responder is null || !responder.RespondsToSelector(HasMarkedTextSelector)) return false;
        // WKWebView answers NSTextInputClient's hasMarkedText; read the BOOL through key-value coding.
        try { return responder.ValueForKey(new NSString("hasMarkedText")) is NSNumber { BoolValue: true }; }
        catch (ObjCException) { return false; }
    }

    // ---- Focus order: From, To, Cc, Bcc, Subject, body ----

    private void ConfigureKeyViewLoop()
    {
        _fromPopup.NextKeyView = _toField;
        _toField.NextKeyView = _ccField;
        _ccField.NextKeyView = _bccField;
        _bccField.NextKeyView = _subjectField;
        _subjectField.NextKeyView = _webView;
    }

    /// <summary>Tab in the subject moves into the message body (Windows SubjectTextBoxPreviewKeyDown).</summary>
    private bool SubjectCommand(NSControl control, NSTextView textView, Selector selector)
    {
        if (selector.Name != "insertTab:" || _editor is null || _editorDisposed) return false;
        Window()?.MakeFirstResponder(_webView);
        Observe(_editor.FocusEditorAsync(false));
        return true;
    }
}
