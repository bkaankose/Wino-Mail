using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Models.Contacts;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Contacts;

/// <summary>
/// The contacts drag session (Windows ContactsPage ContactsListView_DragItemsStarting): rows write
/// <see cref="PasteboardType"/> items, the session carries the resolved contact ids as a
/// <see cref="ContactDragPackage"/>, and shell pane contact lists take the drop (ContactFilterViewModel).
/// </summary>
internal static class ContactDragPayload
{
    public const string PasteboardType = "app.winomail.contacts";
    private const double CardWidth = 250;
    private const double CardHeight = 56;
    private const double BadgeSize = 22;
    private const double StackOffset = 4;

    private static NSImage? _rowSnapshot;

    /// <summary>Contact ids in the drag session in progress; empty when nothing is dragged.</summary>
    public static IReadOnlyList<Guid> Current { get; private set; } = [];

#if DEBUG
    /// <summary>The ids the selection would drag, for the contacts-drag-* debug commands.</summary>
    public static Func<IReadOnlyList<Guid>>? DebugSelection { get; set; }
#endif

    public static void Begin(IReadOnlyList<Guid> contactIds, NSImage? rowSnapshot)
    {
        Current = contactIds;
        _rowSnapshot = rowSnapshot;
    }

    public static void End()
    {
        Current = [];
        _rowSnapshot = null;
    }

    public static bool IsContactDrag(INSDraggingInfo info) => info.DraggingPasteboard.Types?.Contains(PasteboardType) == true;

    /// <summary>The dragged contact ids when <paramref name="info"/> is a Wino contacts drag; otherwise empty.</summary>
    public static IReadOnlyList<Guid> From(INSDraggingInfo info) => IsContactDrag(info) ? Current : [];

    /// <summary>The data a ContactFilterViewModel drop target reads (Windows DataPackage properties).</summary>
    public static IReadOnlyDictionary<string, object> DataProperties(IReadOnlyList<Guid> contactIds)
        => new Dictionary<string, object> { [ContactDragPackage.DataPropertyName] = new ContactDragPackage(contactIds) };

    public static NSArray ItemClasses() => NSArray.FromNSObjects(new ObjCRuntime.INativeObject[] { new ObjCRuntime.Class(typeof(NSPasteboardItem)) });

    public static NSPasteboardItem CreatePasteboardItem(Guid contactId)
    {
        var item = new NSPasteboardItem();
        item.SetStringForType(contactId.ToString("N"), PasteboardType);
        return item;
    }

    /// <summary>
    /// The drag image: the first row on a card, stacked for several contacts with a count badge, and an
    /// optional caption chip ("Add to Family" in the accent, grey when <paramref name="refused"/>).
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

    /// <summary>A bitmap of a table row for the drag card.</summary>
    public static NSImage? SnapshotRow(NSTableView table, nint row)
    {
        if (row < 0 || table.GetRowView(row, false) is not { } rowView) return null;
        var bounds = rowView.Bounds;
        var rep = rowView.BitmapImageRepForCachingDisplayInRect(bounds);
        if (rep is null) return null;
        rowView.CacheDisplay(bounds, rep);
        var image = new NSImage(bounds.Size);
        image.AddRepresentation(rep);
        return image;
    }
}
