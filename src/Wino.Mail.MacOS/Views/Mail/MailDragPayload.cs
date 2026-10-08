using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The Mac MailDragPackage: mails dragged from the mail list to a shell pane folder. The pasteboard
/// carries the custom <see cref="PasteboardType"/> with the mail unique ids so only Wino views accept
/// the drag; the mails themselves stay in process for the drop (Windows keeps the package in the
/// DataPackage properties the same way). Also draws the drag image: the top row's snapshot on a card,
/// offset cards behind it for several mails, a count badge and, over a folder, the move caption.
/// </summary>
internal static class MailDragPayload
{
    public const string PasteboardType = "app.winomail.mails";
    private const double CardWidth = 250;
    private const double CardHeight = 56;
    private const double BadgeSize = 22;
    private const double StackOffset = 4;

    private static NSImage? _rowSnapshot;

    /// <summary>Mails in the drag session in progress; empty when nothing is dragged.</summary>
    public static IReadOnlyList<MailCopy> Current { get; private set; } = [];

#if DEBUG
    /// <summary>The mail list selection, for the drag-validate and drag-drop debug commands.</summary>
    public static Func<IReadOnlyList<MailCopy>>? DebugSelection { get; set; }
#endif

    public static void Begin(IReadOnlyList<MailCopy> mails, NSImage? rowSnapshot)
    {
        Current = mails;
        _rowSnapshot = rowSnapshot;
    }

    public static void End()
    {
        Current = [];
        _rowSnapshot = null;
    }

    /// <summary>The dragged mails when <paramref name="info"/> is a Wino mail drag; otherwise empty.</summary>
    public static IReadOnlyList<MailCopy> From(INSDraggingInfo info)
        => info.DraggingPasteboard.Types?.Contains(PasteboardType) == true ? Current : [];

    /// <summary>The class filter for enumerating the session's dragging items (each one is an NSPasteboardItem).</summary>
    public static NSArray ItemClasses() => NSArray.FromNSObjects(new ObjCRuntime.INativeObject[] { new ObjCRuntime.Class(typeof(NSPasteboardItem)) });

    /// <summary>The pasteboard item for the drag: the mail unique ids, one per line.</summary>
    public static NSPasteboardItem CreatePasteboardItem(IEnumerable<MailCopy> mails)
    {
        var item = new NSPasteboardItem();
        item.SetStringForType(string.Join('\n', mails.Select(static mail => mail.UniqueId.ToString("N"))), PasteboardType);
        return item;
    }

    /// <summary>
    /// Windows CanContinueDragDrop: the folder is a move target, not the selected folder, and at least
    /// one dragged mail belongs to one of its accounts.
    /// </summary>
    public static bool CanDropOn(IBaseFolderMenuItem folder, bool isSelectedFolder, IReadOnlyList<MailCopy> mails)
    {
        if (mails.Count == 0 || isSelectedFolder || !folder.IsMoveTarget) return false;
        var accounts = folder.HandlingFolders.Select(static handling => handling.MailAccountId).ToHashSet();
        return mails.Any(mail => mail.AssignedAccount is { } account && accounts.Contains(account.Id));
    }

    /// <summary>The dragged mails that belong to the folder's accounts (the ones a drop moves).</summary>
    public static List<MailCopy> MailsFor(IBaseFolderMenuItem folder, IReadOnlyList<MailCopy> mails)
    {
        var accounts = folder.HandlingFolders.Select(static handling => handling.MailAccountId).ToHashSet();
        return mails.Where(mail => mail.AssignedAccount is { } account && accounts.Contains(account.Id)).ToList();
    }

    /// <summary>
    /// The drag image for <paramref name="count"/> mails. <paramref name="caption"/> adds the chip under
    /// the card ("Move to Archive" in the accent, or the grey refusal when <paramref name="refused"/>).
    /// </summary>
    public static NSImage CreateImage(int count, string? caption = null, bool refused = false)
    {
        int extraCards = count >= 3 ? 2 : count >= 2 ? 1 : 0;
        var captionAttributes = new NSStringAttributes { Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold), ForegroundColor = NSColor.White };
        var captionText = string.IsNullOrEmpty(caption) ? null : new NSAttributedString(caption, captionAttributes);
        var captionSize = captionText?.Size ?? CGSize.Empty;
        double captionHeight = captionText is null ? 0 : 22;
        double width = Math.Max(CardWidth + extraCards * StackOffset + BadgeSize / 2, captionText is null ? 0 : captionSize.Width + 24);
        double height = BadgeSize / 2 + CardHeight + extraCards * StackOffset + (captionText is null ? 0 : 6 + captionHeight);
        var snapshot = _rowSnapshot;
        var accent = WinoStyle.Accent;

        return NSImage.ImageWithSize(new CGSize(Math.Ceiling(width), Math.Ceiling(height)), true, rect =>
        {
            double top = BadgeSize / 2;
            for (int index = extraCards; index >= 0; index--)
            {
                var card = new CGRect(index * StackOffset, top + index * StackOffset, CardWidth, CardHeight);
                var path = NSBezierPath.FromRoundedRect(card, 6, 6);
                NSColor.WindowBackground.ColorWithAlphaComponent(0.95f).SetFill();
                path.Fill();
                WinoStyle.ZoneStroke.SetStroke();
                path.LineWidth = 1;
                path.Stroke();
                if (index != 0 || snapshot is null) continue;
                NSGraphicsContext.CurrentContext?.SaveGraphicsState();
                NSBezierPath.FromRoundedRect(card.Inset(1, 1), 5, 5).AddClip();
                // The row's leading part (avatar, sender, subject) at its natural size.
                var source = new CGRect(0, 0, Math.Min(snapshot.Size.Width, CardWidth), Math.Min(snapshot.Size.Height, CardHeight));
                snapshot.Draw(new CGRect(card.X, card.Y + (CardHeight - source.Height) / 2, source.Width, source.Height), source, NSCompositingOperation.SourceOver, 0.95f, true, null);
                NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
            }

            if (count > 1)
            {
                var badgeText = new NSAttributedString(count.ToString(), new NSStringAttributes { Font = NSFont.SystemFontOfSize(12, NSFontWeight.Bold), ForegroundColor = NSColor.White });
                var textSize = badgeText.Size;
                double badgeWidth = Math.Max(BadgeSize, textSize.Width + 12);
                var badge = new CGRect(CardWidth - badgeWidth / 2, 0, badgeWidth, BadgeSize);
                NSColor.SystemRed.SetFill();
                NSBezierPath.FromRoundedRect(badge, (nfloat)(BadgeSize / 2), (nfloat)(BadgeSize / 2)).Fill();
                badgeText.DrawAtPoint(new CGPoint(badge.X + (badge.Width - textSize.Width) / 2, badge.Y + (BadgeSize - textSize.Height) / 2));
            }

            if (captionText is not null)
            {
                var chip = new CGRect(0, top + CardHeight + extraCards * StackOffset + 6, captionSize.Width + 20, captionHeight);
                (refused ? NSColor.SystemGray : accent).SetFill();
                NSBezierPath.FromRoundedRect(chip, 5, 5).Fill();
                captionText.DrawAtPoint(new CGPoint(chip.X + 10, chip.Y + (captionHeight - captionSize.Height) / 2));
            }
            return true;
        });
    }
}
