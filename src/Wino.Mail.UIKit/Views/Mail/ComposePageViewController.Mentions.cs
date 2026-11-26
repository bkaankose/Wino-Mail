using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Mail.ViewModels;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// "@" mentions in the body. The engine finds the query and writes the person, and the ViewModel searches people and
/// decides what the list holds; this page only shows that list under the "@" (above it when there is no room below).
/// </summary>
public sealed partial class ComposePageViewController
{
    private const double MentionGap = 4;
    private const double MentionMargin = 10;
    private const double MentionMaximumWidth = 360;

    private ComposeRecipientSuggestionList? _mentionList;
    private NSLayoutConstraint[] _mentionConstraints = [];

    private void BuildMentionList(UIView host)
    {
        _mentionList = new ComposeRecipientSuggestionList(_pictures) { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(_mentionList);
    }

    private void BindMentions()
    {
        if (_mentionList is not { } list) return;
        EventHandler<RecipientSuggestion> picked = (_, suggestion) => Observe(ViewModel.AcceptMentionAsync(suggestion));
        EventHandler<RecipientSuggestion> suppress = (_, suggestion) => SuppressMentionSuggestion(suggestion);
        list.Picked += picked;
        list.SuppressRequested += suppress;

        // UIKit has no event for the mail view resigning; another field starting to edit or the keyboard going away
        // are the moments it can happen, so the list checks the editor's focus then.
        var center = NSNotificationCenter.DefaultCenter;
        NSObject[] observers =
        [
            center.AddObserver(UITextField.TextDidBeginEditingNotification, _ => CheckMentionFocus()),
            center.AddObserver(UITextView.TextDidBeginEditingNotification, _ => CheckMentionFocus()),
            center.AddObserver(UIKeyboard.WillHideNotification, _ => CheckMentionFocus())
        ];
        Bindings.Own(new ActionDisposable(() =>
        {
            list.Picked -= picked;
            list.SuppressRequested -= suppress;
            foreach (var observer in observers) center.RemoveObserver(observer);
        }));

        Bind(nameof(ViewModel.ActiveMentionQuery), vm => vm.ActiveMentionQuery, _ => ShowMentionList());
        Bind(nameof(ViewModel.SelectedMentionIndex), vm => vm.SelectedMentionIndex, index => _mentionList?.SelectRow(index));
    }

    private void ShowMentionList()
    {
        if (_mentionList is not { } list) return;
        if (ViewModel.ActiveMentionQuery is not { } query)
        {
            list.Close();
            return;
        }

        if (_editorDisposed || _webView.Window is null || !_webView.IsFirstResponder)
        {
            ViewModel.CloseMentionList();
            return;
        }

        list.Show(ViewModel.MentionSuggestions.ToList(), query.Text, Translator.Composer_MentionSuggestions);
        PlaceMentionList(list, _zone.ContentView, query);
        list.SelectRow(ViewModel.SelectedMentionIndex);
    }

    private void PlaceMentionList(ComposeRecipientSuggestionList list, UIView host, ComposeMentionQuery query)
    {
        host.LayoutIfNeeded();
        // The engine reports the "@" in the mail view's coordinates, in points.
        var anchor = _webView.ConvertRectToView(new CGRect(query.AnchorX, query.AnchorY, query.AnchorWidth, query.AnchorHeight), host);
        var editor = _webView.ConvertRectToView(_webView.Bounds, host);
        var bounds = host.Bounds;

        var width = Math.Max(0, Math.Min(MentionMaximumWidth, bounds.Width - (2 * MentionMargin)));
        var x = Math.Clamp(anchor.X, MentionMargin, Math.Max(MentionMargin, bounds.Width - MentionMargin - width));
        var roomBelow = editor.GetMaxY() - anchor.GetMaxY() - MentionGap;
        var roomAbove = anchor.GetMinY() - bounds.GetMinY() - MentionGap;
        var below = roomBelow >= list.PreferredHeight || roomBelow >= roomAbove;
        list.LimitHeight(below ? roomBelow : roomAbove);

        NSLayoutConstraint.DeactivateConstraints(_mentionConstraints);
        _mentionConstraints =
        [
            below
                ? list.TopAnchor.ConstraintEqualTo(host.TopAnchor, (nfloat)(anchor.GetMaxY() + MentionGap))
                : list.BottomAnchor.ConstraintEqualTo(host.TopAnchor, (nfloat)(anchor.GetMinY() - MentionGap)),
            list.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor, (nfloat)x),
            list.WidthAnchor.ConstraintEqualTo((nfloat)width)
        ];
        NSLayoutConstraint.ActivateConstraints(_mentionConstraints);
        host.BringSubviewToFront(list);
    }

    private void CheckMentionFocus()
    {
        if (!ViewModel.IsMentionListOpen) return;
        BeginInvokeOnMainThread(() =>
        {
            if (!Bindings.IsDisposed && !_webView.IsFirstResponder) ViewModel.CloseMentionList();
        });
    }

    private void SuppressMentionSuggestion(RecipientSuggestion suggestion)
    {
        ViewModel.RemoveMentionSuggestion(suggestion);
        if (ViewModel.IsMentionListOpen) ShowMentionList();
        Observe(SuppressSuggestionAsync(suggestion));
    }

    private void DisposeMentions()
    {
        _mentionList?.Close();
        NSLayoutConstraint.DeactivateConstraints(_mentionConstraints);
        _mentionConstraints = [];
    }
}
