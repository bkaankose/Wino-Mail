using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The reader's To, Cc and Bcc rows and the contact card (Mac MailRenderingPageViewController.Recipients, Windows
/// InternetAddressTemplate): every recipient is a link that opens a card with the contact picture, name and address;
/// the address copies through <c>CopyClipboardCommand</c>. The sender opens the same card. The card is a popover on
/// iPad and stays a popover on iPhone (no adaptive sheet). A long press on a link offers Copy address.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private MailRecipientRows _recipientRows = new();
    private UIViewController? _contactCard;
    private UIButton? _senderLink;

    private UIView BuildRecipientRows()
    {
        _recipientRows.ContactInvoked += (_, args) => ShowContactCard(args.Name, args.Address, args.Anchor);
        _recipientRows.CopyRequested += (_, address) => CopyAddress(address);
        return _recipientRows;
    }

    /// <summary>A transparent button over the sender's name and address that opens the sender's card.</summary>
    private void AttachSenderLink(UIView nameLines)
    {
        var link = new UIButton(UIButtonType.Custom) { TranslatesAutoresizingMaskIntoConstraints = false };
        link.PrimaryActionTriggered += (_, _) => ShowContactCard(ViewModel.FromName, ViewModel.FromAddress, link);
        link.AddInteraction(new UIContextMenuInteraction(new MenuInteraction(() => string.IsNullOrWhiteSpace(ViewModel.FromAddress) ? null : UIMenu.Create(
        [
            UIAction.Create(Translator.MacOS_Reader_CopyAddress, WinoIcons.Image(WinoIconGlyph.Copy, 18), null, _ => CopyAddress(ViewModel.FromAddress))
        ]))));
        link.AccessibilityTraits = UIAccessibilityTrait.Link;
        WinoAccessibility.Label(link, Translator.ComposerFrom.Trim());
        WinoLayout.Fill(link, nameLines);
        _senderLink = link;
    }

    private void UpdateRecipientRows()
    {
        _recipientRows.SetRecipients(MailRecipientRows.To, Translator.ComposerTo.Trim(), ViewModel.ToItems.ToArray());
        // "Cc:" and "Bcc:" are the protocol labels; Windows shows them untranslated too.
        _recipientRows.SetRecipients(MailRecipientRows.Cc, "Cc:", ViewModel.CcItems.ToArray());
        _recipientRows.SetRecipients(MailRecipientRows.Bcc, "Bcc:", ViewModel.BccItems.ToArray());
        if (_senderLink is not null)
        {
            var sender = string.IsNullOrWhiteSpace(ViewModel.FromName) ? ViewModel.FromAddress : $"{ViewModel.FromName} <{ViewModel.FromAddress}>";
            WinoAccessibility.Label(_senderLink, $"{Translator.ComposerFrom.Trim()} {sender}");
        }
    }

    private void CopyAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        Observe(ViewModel.CopyClipboardCommand.ExecuteAsync(address));
    }

    private void ShowContactCard(string? name, string? address, UIView anchor)
    {
        if (string.IsNullOrWhiteSpace(address) || anchor.Window is null) return;
        CloseContactCard();

        var picture = new WinoContactPicture(40);
        var display = string.IsNullOrWhiteSpace(name) ? address : name;
        picture.SetIdentity(display, address);
        picture.Image = _pictures.Get(address);
        var nameLabel = WinoStyle.Label(display, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 2);
        var addressLink = new MailLinkButton(address, WinoStyle.Description);
        if (addressLink.Configuration is { } configuration)
        {
            configuration.Image = WinoIcons.Image(WinoIconGlyph.Copy, 14, WinoStyle.Accent, Translator.MacOS_Reader_CopyAddress);
            configuration.ImagePlacement = NSDirectionalRectEdge.Trailing;
            configuration.ImagePadding = 6;
            addressLink.Configuration = configuration;
        }
        WinoAccessibility.Label(addressLink, $"{Translator.MacOS_Reader_CopyAddress} {address}");
        addressLink.PrimaryActionTriggered += (_, _) =>
        {
            CopyAddress(address);
            CloseContactCard();
        };
        var text = WinoLayout.VStack(2, nameLabel, addressLink);
        var content = WinoLayout.Insets(WinoLayout.HStack(12, picture, text), 14, 14, 14, 16);
        var card = new UIViewController { ModalPresentationStyle = UIModalPresentationStyle.Popover };
        card.View = new UIView();
        WinoLayout.Fill(content, card.View);
        var size = content.SystemLayoutSizeFittingSize(new CGSize(340, 0), 1000, 50);
        card.PreferredContentSize = new CGSize(Math.Min(340, Math.Max(220, (double)size.Width)), size.Height);
        if (card.PopoverPresentationController is { } popover)
        {
            popover.SourceView = anchor;
            popover.SourceRect = anchor.Bounds;
            popover.PermittedArrowDirections = UIPopoverArrowDirection.Up | UIPopoverArrowDirection.Down;
            popover.Delegate = KeepPopover.Instance;
        }
        _contactCard = card;
        PresentViewController(card, true, null);
    }

    /// <summary>Closes the card if it is still on screen.</summary>
    private void CloseContactCard()
    {
        var card = _contactCard;
        _contactCard = null;
        if (card?.PresentingViewController is not null) card.DismissViewController(true, null);
    }

    /// <summary>Keeps the contact card a popover on iPhone instead of the adaptive sheet.</summary>
    private sealed class KeepPopover : UIPopoverPresentationControllerDelegate
    {
        public static readonly KeepPopover Instance = new();

        public override UIModalPresentationStyle GetAdaptivePresentationStyle(UIPresentationController controller, UITraitCollection traitCollection)
            => UIModalPresentationStyle.None;
    }
}

/// <summary>
/// To, Cc and Bcc rows (Mac MailRecipientRows): a label and a wrapping row of recipient links. A row shows at most
/// <see cref="TokenLimit"/> recipients and a "{0} more" link that expands it in place until the next message.
/// </summary>
internal sealed class MailRecipientRows : UIView
{
    public const int To = 0;
    public const int Cc = 1;
    public const int Bcc = 2;
    private const int TokenLimit = 8;

    private readonly Row[] _rows;

    private sealed class Row
    {
        public required UIView Container { get; init; }
        public required UILabel Label { get; init; }
        public required WinoFlowView Flow { get; init; }
        public AccountContactViewModel[] Contacts { get; set; } = [];
        public bool Expanded { get; set; }
    }

    public sealed record ContactInvokedArgs(string Name, string Address, UIView Anchor);

    public MailRecipientRows()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _rows = new Row[3];
        var stack = WinoLayout.VStack(4);
        stack.Alignment = UIStackViewAlignment.Fill;
        for (int index = 0; index < _rows.Length; index++)
        {
            var label = WinoStyle.Label(string.Empty, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
            label.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
            label.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
            var flow = new WinoFlowView { Spacing = 6, LineSpacing = 0 };
            var container = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
            container.AddSubview(label);
            container.AddSubview(flow);
            NSLayoutConstraint.ActivateConstraints(
            [
                label.LeadingAnchor.ConstraintEqualTo(container.LeadingAnchor),
                label.TopAnchor.ConstraintEqualTo(container.TopAnchor, 4),
                label.BottomAnchor.ConstraintLessThanOrEqualTo(container.BottomAnchor),
                flow.LeadingAnchor.ConstraintEqualTo(label.TrailingAnchor, 6),
                flow.TrailingAnchor.ConstraintEqualTo(container.TrailingAnchor),
                flow.TopAnchor.ConstraintEqualTo(container.TopAnchor),
                flow.BottomAnchor.ConstraintEqualTo(container.BottomAnchor)
            ]);
            stack.AddArrangedSubview(container);
            _rows[index] = new Row { Container = container, Label = label, Flow = flow };
        }
        WinoLayout.Fill(stack, this);
        Hidden = true;
    }

    public event EventHandler<ContactInvokedArgs>? ContactInvoked;

    public event EventHandler<string>? CopyRequested;

    public void SetRecipients(int row, string label, IReadOnlyList<AccountContactViewModel> contacts)
    {
        var target = _rows[row];
        target.Label.Text = label;
        if (!SameContacts(target.Contacts, contacts))
        {
            target.Contacts = contacts.ToArray();
            Rebuild(target);
        }
        target.Container.Hidden = target.Contacts.Length == 0;
        Hidden = _rows.All(static candidate => candidate.Container.Hidden);
    }

    public void ResetExpansion()
    {
        foreach (var row in _rows)
        {
            if (!row.Expanded) continue;
            row.Expanded = false;
            Rebuild(row);
        }
    }

    private static bool SameContacts(IReadOnlyList<AccountContactViewModel> current, IReadOnlyList<AccountContactViewModel> next)
    {
        if (current.Count != next.Count) return false;
        for (int index = 0; index < current.Count; index++)
            if (!ReferenceEquals(current[index], next[index])) return false;
        return true;
    }

    private void Rebuild(Row row)
    {
        var views = new List<UIView>();
        var shown = row.Expanded ? row.Contacts : row.Contacts.Take(TokenLimit).ToArray();
        foreach (var contact in shown) views.Add(Token(contact));
        int hidden = row.Contacts.Length - shown.Length;
        if (hidden > 0)
        {
            var more = new MailLinkButton(string.Format(Translator.MacOS_Reader_MoreRecipients, hidden), WinoStyle.Fixed(13, UIFontWeight.Medium));
            more.PrimaryActionTriggered += (_, _) =>
            {
                row.Expanded = true;
                Rebuild(row);
            };
            views.Add(more);
        }
        var previous = row.Flow.Subviews;
        row.Flow.SetItems(views);
        foreach (var old in previous) old.Dispose();
    }

    private MailLinkButton Token(AccountContactViewModel contact)
    {
        var text = contact.IsMe ? Translator.AccountContactNameYou : contact.ShortDisplayName;
        var token = new MailLinkButton(text ?? string.Empty);
        WinoAccessibility.Label(token, contact.DisplayName);
        var name = contact.Name ?? string.Empty;
        var address = contact.Address ?? string.Empty;
        token.PrimaryActionTriggered += (_, _) => ContactInvoked?.Invoke(this, new ContactInvokedArgs(name, address, token));
        if (address.Length > 0)
        {
            token.AddInteraction(new UIContextMenuInteraction(new MenuInteraction(() => UIMenu.Create(
            [
                UIAction.Create(Translator.MacOS_Reader_CopyAddress, WinoIcons.Image(WinoIconGlyph.Copy, 18), null, _ => CopyRequested?.Invoke(this, address))
            ]))));
        }
        return token;
    }

#if DEBUG
    public string Dump()
        => string.Join(" | ", _rows.Select(static row => $"{row.Label.Text} hidden={row.Container.Hidden} expanded={row.Expanded} count={row.Contacts.Length} tokens=[" +
            string.Join(", ", row.Flow.Subviews.OfType<MailLinkButton>().Select(static token => token.Text)) + "]"));
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ContactInvoked = null;
            CopyRequested = null;
        }
        base.Dispose(disposing);
    }
}
