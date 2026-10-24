using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Calendar;

public sealed partial class WinoCalendarSurfaceView
{
    private const double OverflowTileHeight = 28;
    private const double OverflowTileGap = 4;
    private const double OverflowListWidth = 260;
    private const double OverflowListPadding = 8;
    private const double OverflowListMaxHeight = 360;

    private NSPopover? _overflowPopover;

    private static void DrawMoreLinks(IReadOnlyList<CalendarOverflowCell> overflows)
    {
        if (overflows.Count == 0) return;
        var paragraph = new NSMutableParagraphStyle { LineBreakMode = NSLineBreakMode.TruncatingTail, Alignment = NSTextAlignment.Left };
        var attributes = new NSStringAttributes { Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold), ForegroundColor = WinoStyle.Accent, ParagraphStyle = paragraph };
        foreach (var overflow in overflows)
        {
            var text = new NSAttributedString(overflow.Text, attributes);
            double height = text.Size.Height;
            var bounds = overflow.Bounds;
            text.DrawInRect(new CGRect(bounds.X + 4, bounds.Y + (bounds.Height - height) / 2, Math.Max(0, bounds.Width - 8), height));
        }
    }

    private bool TryShowOverflow(NSView host, IReadOnlyList<CalendarOverflowCell> overflows, CGPoint point)
    {
        var overflow = overflows.FirstOrDefault(o => o.Bounds.Contains(point));
        if (overflow is null) return false;
        ShowOverflow(host, overflow);
        return true;
    }

    private void ShowOverflow(NSView host, CalendarOverflowCell overflow)
    {
        CloseOverflow();
        int count = overflow.HiddenItems.Count;
        if (count == 0) return;

        double contentHeight = OverflowListPadding * 2 + count * OverflowTileHeight + (count - 1) * OverflowTileGap;
        var list = new OverflowListView { Frame = new CGRect(0, 0, OverflowListWidth + OverflowListPadding * 2, contentHeight) };
        for (int index = 0; index < count; index++)
        {
            var item = overflow.HiddenItems[index];
            var tile = new WinoCalendarItemView
            {
                Item = item,
                Kind = CalendarPlacementKind.AllDay,
                Date = overflow.Date,
                Model = Tile(item, overflow.Date),
                Frame = new CGRect(OverflowListPadding, OverflowListPadding + index * (OverflowTileHeight + OverflowTileGap), OverflowListWidth, OverflowTileHeight)
            };
            tile.Clicked += (sender, native) => { CloseOverflow(); RaiseItemClicked((WinoCalendarItemView)sender!, native, 1); };
            tile.DoubleClicked += (sender, native) => { CloseOverflow(); RaiseItemClicked((WinoCalendarItemView)sender!, native, 2); };
            tile.RightClicked += (sender, native) => RaiseItemClicked((WinoCalendarItemView)sender!, native, -1);
            list.AddSubview(tile);
        }

        NSView content = list;
        if (contentHeight > OverflowListMaxHeight)
        {
            content = new NSScrollView
            {
                Frame = new CGRect(0, 0, list.Frame.Width, OverflowListMaxHeight),
                DocumentView = list,
                HasVerticalScroller = true,
                HasHorizontalScroller = false,
                AutohidesScrollers = true,
                DrawsBackground = false,
                BorderType = NSBorderType.NoBorder
            };
        }
        content.AccessibilityLabel = CalendarItemOverflow.FormatAccessibleName(count, overflow.Date, _settings?.CultureInfo);

        var popover = new NSPopover
        {
            Behavior = NSPopoverBehavior.Transient,
            Animates = true,
            ContentViewController = new NSViewController { View = content },
            ContentSize = content.Frame.Size
        };
        _overflowPopover = popover;
        popover.Show(overflow.Bounds, host, NSRectEdge.MaxYEdge);
    }

    private void CloseOverflow()
    {
        var popover = _overflowPopover;
        _overflowPopover = null;
        if (popover is { Shown: true }) popover.Close();
    }

    private sealed class OverflowListView : NSView
    {
        public override bool IsFlipped => true;
    }
}
