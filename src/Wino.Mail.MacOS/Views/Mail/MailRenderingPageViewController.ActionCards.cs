using System.Collections.Specialized;
using AppKit;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Mail.ActionCards;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The action cards of the reader (Windows MailRenderingPage action cards): the cards the shared view model builds from
/// the sender's structured data, shown under the sender row above the message body.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private readonly NSStackView _actionCardsHost = WinoLayout.VStack(8);

    private void InitializeActionCards()
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => RebuildActionCards();
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
