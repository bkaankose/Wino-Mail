using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Shell;
using Wino.Mail.UIKit.Views.Contacts;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Pad.Contacts;

/// <summary>
/// The iPad Contacts mode (Mac Revision 2: list with detail pane): the sidebar shows the contacts filters (the shell's
/// generic rows), the list column this page and the reader column the contact card, which follows the selection. Search is
/// the toolbar field (<see cref="IShellSearchTarget"/>). The editor is a form sheet (PadPageRegistry).
/// </summary>
public sealed class PadContactsPageViewController(ContactsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
    UIKitNavigationService navigation, IPictureStorageService pictures, IExternalLauncher launcher)
    : ContactsPageViewController(viewModel, dispatcher, logger, navigation, pictures, launcher)
{
    private ContactCardViewController? _card;

    protected override bool UsesSearchController => false;

    protected override bool ShowsFilterMenu => false;

    protected override void ContactActivated(AccountContactViewModel contact) => EnsureCard();

    protected override void SelectedContactChanged(AccountContactViewModel? contact) => EnsureCard();

    /// <summary>
    /// Puts the card in the reader column unless it is already there. The frame releases and disposes whatever it holds when
    /// the mode changes, so a card that left the frame is never reused.
    /// </summary>
    private void EnsureCard()
    {
        if (IsReleased || Navigation.Shell is not PadShellViewController shell || shell.ActiveMode != Wino.Core.Domain.Enums.WinoApplicationMode.Contacts) return;
        if (_card is not null && ReferenceEquals(shell.ReaderFrame.Page, _card)) return;
        _card?.Release();
        _card = CreateCard(showsEmptyState: true);
        shell.ShowInReader(_card);
    }

    public override async Task ReleaseAsync()
    {
        var card = _card;
        _card = null;
        await base.ReleaseAsync();
        if (card is null) return;
        Task clear = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            card.Release();
            if (Navigation.Shell is PadShellViewController shell && ReferenceEquals(shell.ReaderFrame.Page, card)) clear = shell.ReaderFrame.ClearAsync();
        });
        await clear;
    }
}
