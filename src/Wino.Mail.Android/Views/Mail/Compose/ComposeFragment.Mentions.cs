using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Renderer.Android;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// "@" mentions in the body. The engine finds the query and writes the person, and the ViewModel searches people and
/// decides what the list holds; this page only shows that list under the "@" (above it when there is no room below),
/// in the recipient suggestion rows. The list never takes focus, so the keyboard stays on the body.
/// </summary>
public sealed partial class ComposeFragment
{
    private const int MentionMaximumRows = 5;
    private const int MentionGapDp = 4;
    private const int MentionMaximumWidthDp = 360;

    private ListPopupWindow? _mentionPopup;
    private ComposeSuggestionAdapter? _mentionAdapter;
    private bool _placingMentionPopup;

    private void BuildMentions(Context context, ComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        if (_web is not { } web) return;
        var adapter = _mentionAdapter = new ComposeSuggestionAdapter(context);
        var popup = _mentionPopup = new ListPopupWindow(context)
        {
            AnchorView = web,
            InputMethodMode = ListPopupWindowInputMethodMode.Needed,
            Modal = false
        };
        popup.SetAdapter(adapter);
        popup.SetBackgroundDrawable(ContextCompat.GetDrawable(context, Resource.Color.wino_card));

        EventHandler<AdapterView.ItemClickEventArgs> picked = (_, args) =>
        {
            if (adapter.ItemAt(args.Position) is { } suggestion) Observe(viewModel.AcceptMentionAsync(suggestion));
        };
        // A touch outside the list dismisses it by itself; the ViewModel closes with it so the next letter reopens it.
        EventHandler dismissed = (_, _) =>
        {
            if (!_placingMentionPopup) viewModel.CloseMentionList();
        };
        popup.ItemClick += picked;
        popup.DismissEvent += dismissed;

        var focus = new MentionFocus(this);
        var observer = web.ViewTreeObserver;
        observer?.AddOnGlobalFocusChangeListener(focus);

        Bindings.Own(new ActionDisposable(() =>
        {
            popup.ItemClick -= picked;
            popup.DismissEvent -= dismissed;
            popup.Dismiss();
            if (observer?.IsAlive == true) observer.RemoveOnGlobalFocusChangeListener(focus);
            _mentionPopup = null;
            _mentionAdapter = null;
        }));

        Bind(viewModel, dispatcher, nameof(ComposePageViewModel.ActiveMentionQuery), vm => vm.ActiveMentionQuery, _ => ShowMentionList());
        Bind(viewModel, dispatcher, nameof(ComposePageViewModel.SelectedMentionIndex), vm => vm.SelectedMentionIndex, SelectMentionRow);
    }

    private void ShowMentionList()
    {
        if (_mentionPopup is not { } popup || _mentionAdapter is not { } adapter || ViewModel is not { } viewModel) return;
        if (viewModel.ActiveMentionQuery is not { } query)
        {
            HideMentionPopup(popup);
            return;
        }

        if (_editorDisposed || _web is not { IsAttachedToWindow: true, HasFocus: true } web)
        {
            viewModel.CloseMentionList();
            return;
        }

        adapter.SetItems(viewModel.MentionSuggestions.ToList());
        PlaceMentionPopup(popup, web, query, adapter.Count);
        SelectMentionRow(viewModel.SelectedMentionIndex);
    }

    private void PlaceMentionPopup(ListPopupWindow popup, MailView web, ComposeMentionQuery query, int count)
    {
        var context = web.Context!;
        // The engine reports the "@" in the mail view's coordinates in dp; the popup is placed in pixels.
        var density = WinoUnits.Density(context);
        var anchorLeft = (int)(query.AnchorX * density);
        var anchorTop = (int)(query.AnchorY * density);
        var anchorBottom = (int)((query.AnchorY + query.AnchorHeight) * density);
        var gap = WinoUnits.Dp(context, MentionGapDp);
        var rowHeight = WinoTokens.Px(context, Resource.Dimension.wino_compose_suggestion_min_height);

        var location = new int[2];
        web.GetLocationInWindow(location);
        var preferred = Math.Min(count, MentionMaximumRows) * rowHeight;
        var roomBelow = web.Height - anchorBottom - gap;
        var roomAbove = location[1] + anchorTop - gap;
        var below = roomBelow >= preferred || roomBelow >= roomAbove;
        var height = Math.Max(rowHeight, Math.Min(preferred, below ? roomBelow : roomAbove));

        var width = Math.Min(WinoUnits.Dp(context, MentionMaximumWidthDp), web.Width);
        popup.Width = width;
        popup.Height = height;
        popup.HorizontalOffset = Math.Clamp(anchorLeft, 0, Math.Max(0, web.Width - width));
        // Offsets are measured from the bottom of the mail view.
        popup.VerticalOffset = below ? anchorBottom + gap - web.Height : anchorTop - gap - height - web.Height;

        // Showing an open list moves and resizes it in place.
        popup.Show();
    }

    private void SelectMentionRow(int index)
    {
        if (_mentionPopup is { IsShowing: true } popup && _mentionAdapter is { } adapter && index >= 0 && index < adapter.Count)
            popup.SetSelection(index);
    }

    private void HideMentionPopup(ListPopupWindow popup)
    {
        _placingMentionPopup = true;
        try { popup.Dismiss(); }
        finally { _placingMentionPopup = false; }
        _mentionAdapter?.SetItems(Array.Empty<RecipientSuggestion>());
    }

    /// <summary>Another field taking focus closes the list; a window without a focused view keeps it (the keyboard opening).</summary>
    private sealed class MentionFocus(ComposeFragment owner) : Java.Lang.Object, ViewTreeObserver.IOnGlobalFocusChangeListener
    {
        public void OnGlobalFocusChanged(View? oldFocus, View? newFocus)
        {
            if (newFocus is null || owner._web is not { } web || ReferenceEquals(newFocus, web)) return;
            owner.ViewModel?.CloseMentionList();
        }
    }
}
