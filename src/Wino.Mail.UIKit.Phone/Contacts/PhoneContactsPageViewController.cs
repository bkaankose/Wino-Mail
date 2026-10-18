using UIKit;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Mail;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.UIKit.Views.Contacts;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Phone.Contacts;

/// <summary>
/// The iPhone Contacts tab (iOS design decisions): the letter-indexed list with a search glyph in the bar and the filters
/// in the shell drawer; a tap pushes the contact card. New and edit open the editor as a full-screen sheet (PhonePageRegistry).
/// </summary>
public sealed class PhoneContactsPageViewController(ContactsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
    UIKitNavigationService navigation, IPictureStorageService pictures, IExternalLauncher launcher)
    : ContactsPageViewController(viewModel, dispatcher, logger, navigation, pictures, launcher), IPhoneFloatingActionPage
{
    private WeakReference<ContactCardViewController>? _card;

    protected override bool UsesSearchController => true;

    // The contact filters are the shell drawer's rows; the bar keeps New contact and search.
    protected override bool ShowsFilterMenu => false;

    // New contact is the shell's floating button.
    protected override bool ShowsAddButton => false;

    void IPhoneFloatingActionPage.FloatingActionInvoked() => AddContact();

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        // Search sits behind the bar's search glyph on every tab.
        if (NavigationItem.SearchController is { } search) PhoneBarItems.BarSearch(NavigationItem, search);
    }

    protected override void ContactActivated(AccountContactViewModel contact)
    {
        if (NavigationController is not { } stack) return;
        if (_card is not null && _card.TryGetTarget(out var open) && Array.IndexOf(stack.ViewControllers ?? [], open) >= 0) return;
        var card = CreateCard(showsEmptyState: false);
        card.NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        card.SelectionCleared += (_, _) =>
        {
            if (ReferenceEquals(stack.TopViewController, card)) stack.PopViewController(true);
        };
        _card = new WeakReference<ContactCardViewController>(card);
        PhoneShellViewController.PrepareForTransition(card, stack);
        stack.PushViewController(card, true);
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        if (IsMovingToParentViewController) return;
        // Back from the card: it stops following the selection, and the row is no longer "open".
        if (_card is not null && _card.TryGetTarget(out var card))
        {
            card.Release();
            _card = null;
        }
        DeselectVisibleRow();
    }
}
