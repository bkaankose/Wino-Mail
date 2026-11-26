using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.MacOS.Views.Mail.Compose;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// "@" mentions in the body. The engine finds the query and writes the person, and the ViewModel searches people and
/// decides what the list holds; this page only shows that list under the "@" in the recipient suggestion panel.
/// </summary>
public sealed partial class ComposePageViewController
{
    private ComposeRecipientSuggestionPopup? _mentionPopup;
    private IDisposable? _mentionFocusObserver;

    private void BindMentions()
    {
        _mentionPopup = new ComposeRecipientSuggestionPopup(_pictures);
        _mentionPopup.Picked += (_, suggestion) => Observe(ViewModel.AcceptMentionAsync(suggestion));
        _mentionPopup.HoverChanged += (_, row) => ViewModel.SelectedMentionIndex = row;
        _mentionPopup.SuppressRequested += (_, suggestion) => SuppressMentionSuggestion(suggestion);
        Bind(nameof(ViewModel.ActiveMentionQuery), vm => vm.ActiveMentionQuery, _ => ShowMentionList());
        Bind(nameof(ViewModel.SelectedMentionIndex), vm => vm.SelectedMentionIndex, index => _mentionPopup?.SelectRow(index));
    }

    private void ShowMentionList()
    {
        if (_mentionPopup is null) return;
        if (ViewModel.ActiveMentionQuery is not { } query)
        {
            HideMentionList();
            return;
        }

        if (_editorDisposed || View.Window is not { } window || !window.IsKeyWindow || !IsInEditor(window.FirstResponder))
        {
            ViewModel.CloseMentionList();
            return;
        }

        // The engine reports the "@" in the mail view's flipped coordinates, in points.
        var anchor = new CGRect(query.AnchorX, query.AnchorY, query.AnchorWidth, query.AnchorHeight);
        _mentionPopup.Show(_webView, anchor, ViewModel.MentionSuggestions.ToList(), query.Text, Translator.Composer_MentionSuggestions);
        _mentionPopup.SelectRow(ViewModel.SelectedMentionIndex);
        WatchEditorFocus(window);
    }

    /// <summary>The list closes as soon as anything else in the window takes the keyboard from the editor.</summary>
    private void WatchEditorFocus(NSWindow window)
    {
        if (_mentionFocusObserver is not null) return;
        _mentionFocusObserver = window.AddObserver("firstResponder", NSKeyValueObservingOptions.New, _ =>
        {
            if (!IsInEditor(window.FirstResponder)) ViewModel.CloseMentionList();
        });
    }

    private void HideMentionList()
    {
        _mentionFocusObserver?.Dispose();
        _mentionFocusObserver = null;
        _mentionPopup?.Close();
    }

    private void SuppressMentionSuggestion(RecipientSuggestion suggestion)
    {
        ViewModel.RemoveMentionSuggestion(suggestion);
        if (ViewModel.IsMentionListOpen) ShowMentionList();
        Observe(SuppressSuggestionAsync(suggestion));
    }

    private void DisposeMentions()
    {
        HideMentionList();
        _mentionPopup?.Dispose();
        _mentionPopup = null;
    }
}
