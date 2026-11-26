using System.Collections.Specialized;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Views.Mail.ActionCards;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The booking and order cards above the message (Windows MailRenderingPage action cards) over
/// <see cref="MailRenderingPageViewModel.ActionCards"/>. The cards are rebuilt when the ViewModel's collection changes.
/// </summary>
public sealed partial class MailReaderFragment
{
    private LinearLayout? _actionCards;

    private void ObserveActionCards(MailRenderingPageViewModel viewModel, IDispatcher dispatcher)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = dispatcher.ExecuteOnUIThread(() =>
        {
            if (!Bindings.IsDisposed) UpdateActionCards();
        });
        viewModel.ActionCards.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => viewModel.ActionCards.CollectionChanged -= handler));
        UpdateActionCards();
    }

    private void UpdateActionCards()
    {
        if (_actionCards is null || ViewModel is not { } viewModel || View?.Context is not { } context) return;
        _actionCards.RemoveAllViews();
        foreach (var card in viewModel.ActionCards)
        {
            _actionCards.AddView(new ReaderActionCardView(context, card, card.IsExpanded,
                expanded => viewModel.AreActionCardsExpanded = expanded,
                action => Observe(viewModel.InvokeActionCardActionCommand.ExecuteAsync(action)),
                text => Observe(viewModel.CopyClipboardCommand.ExecuteAsync(text))));
        }
        _actionCards.Visibility = _actionCards.ChildCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }
}
