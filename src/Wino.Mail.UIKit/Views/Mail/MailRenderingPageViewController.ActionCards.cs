using System.Collections.Specialized;
using UIKit;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.ActionCards;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The action cards of the reader (Windows MailRenderingPage action cards, Mac MailRenderingPageViewController.ActionCards):
/// the cards the shared view model builds from the sender's structured data, shown under the recipients above the body.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private void InitializeActionCards()
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() => { if (!Bindings.IsDisposed) RebuildActionCards(); });
        ViewModel.ActionCards.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => ViewModel.ActionCards.CollectionChanged -= handler));
        RebuildActionCards();
    }

    private void RebuildActionCards()
    {
        foreach (var view in _actionCardsHost.ArrangedSubviews)
        {
            _actionCardsHost.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }

        foreach (var card in ViewModel.ActionCards)
        {
            _actionCardsHost.AddArrangedSubview(new ActionCardView(
                card,
                expanded => ViewModel.AreActionCardsExpanded = expanded,
                action => Observe(ViewModel.InvokeActionCardActionCommand.ExecuteAsync(action)),
                value => Observe(ViewModel.CopyClipboardCommand.ExecuteAsync(value))));
        }

        _actionCardsHost.Hidden = ViewModel.ActionCards.Count == 0;
    }
}
