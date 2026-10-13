using Foundation;
using ObjCRuntime;
using UIKit;
using Wino.Core.Domain;
using Wino.Editor.WebKit;
using Wino.Mail.UIKit.Views.Mail.Compose;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Find in message (Mac Find partial, design board ComposeFindBar) and the composer's hardware keyboard shortcuts:
/// ⌘↩ sends, ⌘F finds, ⌥⌘F finds and replaces, ⌘G / ⇧⌘G step, ⌘E uses the selection, Esc closes the bar (or cancels a
/// running rewrite, or closes the suggestion list). UIKit routes key commands only while focus is in this composer.
/// </summary>
public sealed partial class ComposePageViewController
{
    private ComposeFindBar? _findBar;
    private CancellationTokenSource? _findDebounce;
    private bool _findRunning;
    private bool _keyCommandsInstalled;

    private UIView BuildFindBar()
    {
        var bar = new ComposeFindBar { Hidden = true };
        bar.QueryChanged += (_, _) => ScheduleFind(EditorFindDirection.Start, 120);
        bar.NextRequested += (_, _) => Observe(RunFindAsync(EditorFindDirection.Next));
        bar.PreviousRequested += (_, _) => Observe(RunFindAsync(EditorFindDirection.Previous));
        bar.ReplaceRequested += (_, _) => Observe(ReplaceAsync());
        bar.ReplaceAllRequested += (_, _) => Observe(ReplaceAllAsync());
        bar.CloseRequested += (_, _) => CloseFind();
        _findBar = bar;
        return bar;
    }

    internal bool IsFindVisible => _findBar is { Hidden: false };

    private void ShowFind(bool replace, string? seed = null)
    {
        if (_findBar is not { } bar) return;
        if (seed is not null) bar.SetQuery(seed);
        bar.SetReplaceVisible(replace || (bar.IsReplaceVisible && !bar.Hidden));
        if (bar.Hidden)
        {
            UIView.Animate(UIAccessibility.IsReduceMotionEnabled ? 0 : 0.15, () =>
            {
                bar.Hidden = false;
                _content.LayoutIfNeeded();
            });
        }
        bar.FocusSearch();
        if (bar.Query.Length > 0) ScheduleFind(EditorFindDirection.Start, 0);
        else bar.SetResult(0, 0);
    }

    private void CloseFind()
    {
        if (_findBar is not { Hidden: false } bar) return;
        _findDebounce?.Cancel();
        bar.EndEditing(true);
        UIView.Animate(UIAccessibility.IsReduceMotionEnabled ? 0 : 0.15, () =>
        {
            bar.Hidden = true;
            _content.LayoutIfNeeded();
        });
        // Leaves the caret on the current match, in the editor.
        if (_editor is { } editor && !_editorDisposed) Observe(CloseFindAsync(editor));
    }

    private async Task CloseFindAsync(WebKitHtmlMailEditorSession editor)
    {
        await editor.ClearFindAsync();
        await editor.FocusEditorAsync(true);
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
        if (_editor is not { } editor || _editorDisposed || _findBar is not { } bar) return;
        var query = bar.Query;
        if (query.Length == 0 && direction != EditorFindDirection.Start)
        {
            ShowFind(false);
            return;
        }
        _findRunning = true;
        EditorFindResult result;
        try { result = await editor.FindAsync(query, bar.IgnoreCase, bar.WholeWords, direction); }
        finally { _findRunning = false; }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed) return;
            bar.SetResult(result.Index, result.Count);
            if (query.Length > 0) Announce(bar.CounterText);
        });
    }

    private async Task ReplaceAsync()
    {
        if (_editor is not { } editor || _editorDisposed || _findBar is not { } bar || bar.Query.Length == 0) return;
        var result = await editor.ReplaceAsync(bar.Replacement);
        await Dispatcher.ExecuteOnUIThread(() => bar.SetResult(result.Index, result.Count));
    }

    private async Task ReplaceAllAsync()
    {
        if (_editor is not { } editor || _editorDisposed || _findBar is not { } bar || bar.Query.Length == 0) return;
        await editor.FindAsync(bar.Query, bar.IgnoreCase, bar.WholeWords, EditorFindDirection.Start);
        await editor.ReplaceAllAsync(bar.Replacement);
        await RunFindAsync(EditorFindDirection.Start);
    }

    /// <summary>Edits move matches; the counter follows while the bar is open.</summary>
    private void RefreshFindAfterEdit()
    {
        if (!IsFindVisible || _findRunning || _findBar is not { } bar || bar.Query.Length == 0) return;
        ScheduleFind(EditorFindDirection.Start, 250);
    }

    /// <summary>A short single-line editor selection seeds a newly opened bar.</summary>
    private string? SelectionSeed()
    {
        if (IsFindVisible) return null;
        var selected = _editor?.CurrentState.SelectedText;
        return string.IsNullOrWhiteSpace(selected) || selected.Length > 100 || selected.Contains('\n') ? null : selected.Trim();
    }

    private void UseSelectionForFind()
    {
        var selection = _editor?.CurrentState.SelectedText;
        if (string.IsNullOrWhiteSpace(selection) || _findBar is not { } bar) return;
        bar.SetQuery(selection.Trim());
        if (IsFindVisible) ScheduleFind(EditorFindDirection.Start, 0);
    }

    // ---- Hardware keyboard ----

    private void InstallKeyCommands()
    {
        if (_keyCommandsInstalled) return;
        _keyCommandsInstalled = true;
        AddKeyCommand(Command("\r", UIKeyModifierFlags.Command, "composeSend:", Translator.Buttons_Send));
        AddKeyCommand(Command("f", UIKeyModifierFlags.Command, "composeFind:", Translator.MailOperation_Find));
        AddKeyCommand(Command("f", UIKeyModifierFlags.Command | UIKeyModifierFlags.Alternate, "composeReplace:", Translator.Composer_FindShowReplace));
        AddKeyCommand(Command("g", UIKeyModifierFlags.Command, "composeFindNext:", Translator.Composer_FindNext));
        AddKeyCommand(Command("g", UIKeyModifierFlags.Command | UIKeyModifierFlags.Shift, "composeFindPrevious:", Translator.Composer_FindPrevious));
        AddKeyCommand(Command("e", UIKeyModifierFlags.Command, "composeUseSelection:", null));
        AddKeyCommand(Command(UIKeyCommand.Escape.ToString(), 0, "composeEscape:", null));
    }

    private static UIKeyCommand Command(string input, UIKeyModifierFlags modifiers, string selector, string? title)
    {
        var command = UIKeyCommand.Create(new NSString(input), modifiers, new Selector(selector));
        if (title is not null) command.DiscoverabilityTitle = new NSString(title);
        // The composer's shortcuts win over the web view's own handling (⌘F, Esc).
        command.WantsPriorityOverSystemBehavior = true;
        return command;
    }

    [Export("composeSend:")]
    private void ComposeSendKey(UIKeyCommand command) => Observe(SendOrUploadAsync());

    [Export("composeFind:")]
    private void ComposeFindKey(UIKeyCommand command) => ShowFind(false, SelectionSeed());

    [Export("composeReplace:")]
    private void ComposeReplaceKey(UIKeyCommand command) => ShowFind(true, SelectionSeed());

    [Export("composeFindNext:")]
    private void ComposeFindNextKey(UIKeyCommand command) => Observe(RunFindAsync(EditorFindDirection.Next));

    [Export("composeFindPrevious:")]
    private void ComposeFindPreviousKey(UIKeyCommand command) => Observe(RunFindAsync(EditorFindDirection.Previous));

    [Export("composeUseSelection:")]
    private void ComposeUseSelectionKey(UIKeyCommand command) => UseSelectionForFind();

    [Export("composeEscape:")]
    private void ComposeEscapeKey(UIKeyCommand command)
    {
        if (_suggestionList is { IsVisible: true })
        {
            CloseSuggestions();
            return;
        }
        if (ViewModel.RewriteSession.IsBusy)
        {
            ViewModel.RewriteSession.CancelCommand.Execute(null);
            return;
        }
        if (IsFindVisible) CloseFind();
    }
}
