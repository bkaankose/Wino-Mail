using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.MacOS.Infrastructure;
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
    private NSView? _contactCardContent;
    private Foundation.NSObject? _contactCardObserver;

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
        WinoAccessibility.Label(link, Translator.ComposerFrom.Trim());
        WinoLayout.Fill(link, nameLine);
        _senderLink = link;
    }

    private MailLinkButton? _senderLink;

    /// <summary>
    /// Follows accent changes for the reader's links through the page's binding scope, so the static
    /// event never outlives the page (the links themselves do not subscribe).
    /// </summary>
    private void BindRecipientAccent()
    {
        EventHandler handler = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed) return;
            _senderLink?.RefreshAccent();
            _recipientRows.RefreshAccent();
        });
        WinoStyle.AccentChanged += handler;
        Bindings.Own(new ActionDisposable(() => WinoStyle.AccentChanged -= handler));
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

    private void ShowContactCard(string? name, string? address, NSView anchor)
    {
        if (string.IsNullOrWhiteSpace(address) || anchor.Window is null) return;
        CloseContactCard();

        var picture = new WinoContactPicture(36);
        var display = string.IsNullOrWhiteSpace(name) ? address : name;
        picture.SetIdentity(display, address);
        picture.Image = _pictures.Get(address);
        var nameLabel = WinoStyle.Label(display, WinoStyle.BodyStrong);
        nameLabel.Selectable = true;
        var addressLink = new MailLinkButton(address, NSFont.SystemFontOfSize(12))
        {
            Image = WinoIcons.Image(WinoIconGlyph.Copy, 12, WinoStyle.Accent, Translator.MacOS_Reader_CopyAddress),
            ImagePosition = NSCellImagePosition.ImageTrailing,
            ToolTip = Translator.MacOS_Reader_CopyAddress
        };
        // Links give way to their row by default; the card sizes itself to the full address instead.
        addressLink.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        WinoAccessibility.Label(addressLink, $"{Translator.MacOS_Reader_CopyAddress} {address}");
        addressLink.Activated += (_, _) =>
        {
            CopyAddress(address);
            CloseContactCard();
        };
        var text = WinoLayout.VStack(2, nameLabel, addressLink);
        text.WidthAnchor.ConstraintLessThanOrEqualTo(320).Active = true;
        var stack = WinoLayout.HStack(10, picture, text);
        // A plain container carries the padding: the popover sizes itself from its content view's
        // fitting size, which ignores a stack view's own edge insets.
        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(stack, content, 12, 14, 12, 16);

        var card = new NSPopover
        {
            Behavior = NSPopoverBehavior.Transient,
            Animates = true,
            ContentViewController = new NSViewController { View = content }
        };
        card.ContentSize = content.FittingSize;
        // A transient dismissal releases the card too, outside the popover's own close callback.
        _contactCardObserver = Foundation.NSNotificationCenter.DefaultCenter.AddObserver(NSPopover.DidCloseNotification,
            _ => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                if (ReferenceEquals(_contactCard, card)) CloseContactCard();
            }), card);
        _contactCard = card;
        _contactCardContent = content;
        card.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
    }

    /// <summary>Closes the card and disposes its views (the address link and its closure over this page).</summary>
    private void CloseContactCard()
    {
        var card = _contactCard;
        var content = _contactCardContent;
        var observer = _contactCardObserver;
        _contactCard = null;
        _contactCardContent = null;
        _contactCardObserver = null;
        if (observer is not null) Foundation.NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        if (card is not null)
        {
            card.Close();
            card.Dispose();
        }
        if (content is null) return;
        foreach (var view in Descendants(content)) view.Dispose();
    }

    private static List<NSView> Descendants(NSView root)
    {
        var views = new List<NSView>();
        void Walk(NSView view)
        {
            foreach (var child in view.Subviews) Walk(child);
            views.Add(view);
        }
        Walk(root);
        return views;
    }
}

/// <summary>
/// To, Cc and Bcc rows: a wrapping row that starts with its caption, then the recipient links. The
/// caption is a borderless button like the links, so both share one baseline. A row shows at most
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
        public required WinoFlowView Flow { get; init; }
        public required MailRecipientCaption Caption { get; init; }
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
            var flow = new WinoFlowView { Spacing = 4, LineSpacing = 2, Hidden = true };
            stack.AddArrangedSubview(flow);
            flow.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
            _rows[index] = new Row { Flow = flow, Caption = new MailRecipientCaption() };
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
        target.Caption.Text = label;
        if (!SameContacts(target.Contacts, contacts))
        {
            target.Contacts = contacts.ToArray();
            Rebuild(target);
        }
        target.Flow.Hidden = target.Contacts.Length == 0;
        Hidden = _rows.All(static candidate => candidate.Flow.Hidden);
    }

    /// <summary>Re-applies the accent to the links (the owner follows the accent event).</summary>
    public void RefreshAccent()
    {
        foreach (var row in _rows)
            foreach (var link in row.Flow.Subviews.OfType<MailLinkButton>()) link.RefreshAccent();
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
        var views = new List<NSView> { row.Caption };
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
        foreach (var old in previous) if (!ReferenceEquals(old, row.Caption)) old.Dispose();
    }

    private MailLinkButton Token(AccountContactViewModel contact)
    {
        // Windows ShortNameOrYou without its ";" separator: each recipient is its own link here.
        var text = contact.ShortNameOrYouLabel;
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
        => string.Join(" | ", _rows.Select(static row => $"{row.Caption.Text} hidden={row.Flow.Hidden} expanded={row.Expanded} count={row.Contacts.Length} tokens=[" +
            string.Join(", ", row.Flow.Subviews.OfType<MailLinkButton>().Select(static token => token.Text)) + "]"));
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ContactInvoked = null;
            CopyRequested = null;
            foreach (var row in _rows) row.Caption.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The "To:", "Cc:" or "Bcc:" caption at the start of a recipient row: a borderless button drawn
/// like the recipient links (same cell, same baseline) that takes no clicks or focus.
/// </summary>
internal sealed class MailRecipientCaption : NSButton
{
    public MailRecipientCaption()
    {
        Bordered = false;
        TranslatesAutoresizingMaskIntoConstraints = false;
        SetButtonType(NSButtonType.MomentaryChange);
        AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
    }

    public string Text
    {
        get => Title;
        set => AttributedTitle = new Foundation.NSAttributedString(value ?? string.Empty, new NSStringAttributes
        {
            ForegroundColor = WinoStyle.SecondaryText,
            Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)
        });
    }

    public override bool AcceptsFirstResponder() => false;

    public override NSView? HitTest(CGPoint point) => null;
}
