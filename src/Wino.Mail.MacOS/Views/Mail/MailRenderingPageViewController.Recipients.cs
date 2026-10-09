using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The reader's To, Cc and Bcc rows and the contact card (Windows MailRenderingPage
/// InternetAddressTemplate): every recipient is a link that opens a card with the contact picture,
/// name and address; the address copies through <c>CopyClipboardCommand</c>. The sender opens the same card.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private MailRecipientRows _recipientRows = null!;
    private NSPopover? _contactCard;

    private NSView BuildRecipientRows()
    {
        _recipientRows = new MailRecipientRows();
        _recipientRows.ContactInvoked += (_, args) => ShowContactCard(args.Name, args.Address, args.Anchor);
        _recipientRows.CopyRequested += (_, address) => CopyAddress(address);
        return _recipientRows;
    }

    /// <summary>A transparent link over the sender's name and address that opens the sender's card.</summary>
    private void AttachSenderLink(NSView nameLine)
    {
        var link = new MailLinkButton(string.Empty) { Transparent = true };
        // The overlay follows the labels' size; it must not pull the line narrower.
        link.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        link.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        link.Activated += (_, _) => ShowContactCard(ViewModel.FromName, ViewModel.FromAddress, link);
        link.Menu = new NSMenu { AutoEnablesItems = false };
        link.Menu.AddItem(new NSMenuItem(Translator.MacOS_Reader_CopyAddress, (_, _) => CopyAddress(ViewModel.FromAddress))
        {
            Image = WinoIcons.Image(WinoIconGlyph.Copy, 16)
        });
        WinoAccessibility.Label(link, Translator.ComposerFrom);
        WinoLayout.Fill(link, nameLine);
        _senderLink = link;
    }

    private MailLinkButton? _senderLink;

    private void UpdateRecipientRows()
    {
        _recipientRows.SetRecipients(MailRecipientRows.To, Translator.ComposerTo.Trim(), ViewModel.ToItems.ToArray());
        // "Cc:" and "Bcc:" are the protocol labels; Windows shows them untranslated too.
        _recipientRows.SetRecipients(MailRecipientRows.Cc, "Cc:", ViewModel.CcItems.ToArray());
        _recipientRows.SetRecipients(MailRecipientRows.Bcc, "Bcc:", ViewModel.BccItems.ToArray());
        if (_senderLink is not null)
        {
            var sender = string.IsNullOrWhiteSpace(ViewModel.FromName) ? ViewModel.FromAddress : $"{ViewModel.FromName} <{ViewModel.FromAddress}>";
            WinoAccessibility.Label(_senderLink, $"{Translator.ComposerFrom} {sender}");
        }
    }

    private void CopyAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        Observe(ViewModel.CopyClipboardCommand.ExecuteAsync(address));
    }

    private void ShowContactCard(string? name, string? address, NSView anchor)
    {
        if (string.IsNullOrWhiteSpace(address) || anchor.Window is null) return;
        CloseContactCard();

        var picture = new WinoContactPicture(36);
        var display = string.IsNullOrWhiteSpace(name) ? address : name;
        picture.SetIdentity(display, address);
        var nameLabel = WinoStyle.Label(display, WinoStyle.BodyStrong);
        nameLabel.Selectable = true;
        var addressLink = new MailLinkButton(address, NSFont.SystemFontOfSize(12))
        {
            Image = WinoIcons.Image(WinoIconGlyph.Copy, 12, WinoStyle.Accent, Translator.MacOS_Reader_CopyAddress),
            ImagePosition = NSCellImagePosition.ImageTrailing,
            ToolTip = Translator.MacOS_Reader_CopyAddress
        };
        WinoAccessibility.Label(addressLink, $"{Translator.MacOS_Reader_CopyAddress} {address}");
        addressLink.Activated += (_, _) =>
        {
            CopyAddress(address);
            CloseContactCard();
        };
        var text = WinoLayout.VStack(2, nameLabel, addressLink);
        text.WidthAnchor.ConstraintLessThanOrEqualTo(320).Active = true;
        var content = WinoLayout.HStack(10, picture, text);
        content.EdgeInsets = new NSEdgeInsets(12, 12, 12, 14);

        _contactCard = new NSPopover
        {
            Behavior = NSPopoverBehavior.Transient,
            Animates = true,
            ContentViewController = new NSViewController { View = content }
        };
        _contactCard.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
    }

    private void CloseContactCard()
    {
        if (_contactCard is null) return;
        _contactCard.Close();
        _contactCard.Dispose();
        _contactCard = null;
    }
}

/// <summary>
/// To, Cc and Bcc rows: a label and a wrapping row of recipient links. A row shows at most
/// <see cref="TokenLimit"/> recipients and a "{0} more" link that expands it in place until the next message.
/// </summary>
internal sealed class MailRecipientRows : NSView
{
    public const int To = 0;
    public const int Cc = 1;
    public const int Bcc = 2;
    private const int TokenLimit = 8;

    private readonly Row[] _rows;

    private sealed class Row
    {
        public required NSView Container { get; init; }
        public required NSTextField Label { get; init; }
        public required WinoFlowView Flow { get; init; }
        public AccountContactViewModel[] Contacts { get; set; } = [];
        public bool Expanded { get; set; }
    }

    public sealed record ContactInvokedArgs(string Name, string Address, NSView Anchor);

    public MailRecipientRows()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _rows = new Row[3];
        var stack = WinoLayout.VStack(4);
        for (int index = 0; index < _rows.Length; index++)
        {
            var label = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), WinoStyle.SecondaryText);
            label.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
            label.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
            var flow = new WinoFlowView { Spacing = 4, LineSpacing = 2 };
            var container = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
            container.AddSubview(label);
            container.AddSubview(flow);
            NSLayoutConstraint.ActivateConstraints(
            [
                label.LeadingAnchor.ConstraintEqualTo(container.LeadingAnchor),
                label.TopAnchor.ConstraintEqualTo(container.TopAnchor, 2),
                label.BottomAnchor.ConstraintLessThanOrEqualTo(container.BottomAnchor),
                flow.LeadingAnchor.ConstraintEqualTo(label.TrailingAnchor, 6),
                flow.TrailingAnchor.ConstraintEqualTo(container.TrailingAnchor),
                flow.TopAnchor.ConstraintEqualTo(container.TopAnchor),
                flow.BottomAnchor.ConstraintEqualTo(container.BottomAnchor)
            ]);
            stack.AddArrangedSubview(container);
            container.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
            _rows[index] = new Row { Container = container, Label = label, Flow = flow };
        }
        WinoLayout.Fill(stack, this);
        Hidden = true;
    }

    /// <summary>A recipient link was clicked; the anchor is the link, for the contact card.</summary>
    public event EventHandler<ContactInvokedArgs>? ContactInvoked;

    /// <summary>"Copy address" was chosen from a recipient's menu.</summary>
    public event EventHandler<string>? CopyRequested;

    public void SetRecipients(int row, string label, IReadOnlyList<AccountContactViewModel> contacts)
    {
        var target = _rows[row];
        target.Label.StringValue = label;
        if (!SameContacts(target.Contacts, contacts))
        {
            target.Contacts = contacts.ToArray();
            Rebuild(target);
        }
        target.Container.Hidden = target.Contacts.Length == 0;
        Hidden = _rows.All(static candidate => candidate.Container.Hidden);
    }

    /// <summary>Collapses expanded rows again, for the next message.</summary>
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
        var views = new List<NSView>();
        var shown = row.Expanded ? row.Contacts : row.Contacts.Take(TokenLimit).ToArray();
        foreach (var contact in shown) views.Add(Token(contact));
        int hidden = row.Contacts.Length - shown.Length;
        if (hidden > 0)
        {
            var more = new MailLinkButton(string.Format(Translator.MacOS_Reader_MoreRecipients, hidden), NSFont.SystemFontOfSize(12, NSFontWeight.Medium));
            more.Activated += (_, _) =>
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
        // Windows ShortNameOrYou without its ";" separator: each recipient is its own link here.
        var text = contact.IsMe ? Translator.AccountContactNameYou : contact.ShortDisplayName;
        var token = new MailLinkButton(text ?? string.Empty, NSFont.SystemFontOfSize(12)) { ToolTip = contact.DisplayName };
        WinoAccessibility.Label(token, contact.DisplayName);
        var name = contact.Name ?? string.Empty;
        var address = contact.Address ?? string.Empty;
        token.Activated += (_, _) => ContactInvoked?.Invoke(this, new ContactInvokedArgs(name, address, token));
        token.Menu = new NSMenu { AutoEnablesItems = false };
        token.Menu.AddItem(new NSMenuItem(Translator.MacOS_Reader_CopyAddress, (_, _) => CopyRequested?.Invoke(this, address))
        {
            Image = WinoIcons.Image(WinoIconGlyph.Copy, 16),
            Enabled = address.Length > 0
        });
        return token;
    }

#if DEBUG
    /// <summary>Debug: the rows with their visible tokens.</summary>
    public string Dump()
        => string.Join(" | ", _rows.Select(static row => $"{row.Label.StringValue} hidden={row.Container.Hidden} expanded={row.Expanded} count={row.Contacts.Length} tokens=[" +
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
