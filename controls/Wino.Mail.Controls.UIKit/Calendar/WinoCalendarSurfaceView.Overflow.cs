using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Calendar;

/// <summary>
/// The "n more events" links (Windows CalendarMoreEventsButton, AppKit WinoCalendarSurfaceView.Overflow): a day whose
/// all-day lanes or month cell cannot show every event draws an accent link; a tap opens the hidden events as tiles in
/// a popover anchored to the link, and a tap on a tile raises the surface's item click like a tile on the grid.
/// </summary>
public sealed partial class WinoCalendarSurfaceView
{
    private const double OverflowTileHeight = 32;
    private const double OverflowTileGap = 4;
    private const double OverflowListWidth = 280;
    private const double OverflowListPadding = 12;
    private const double OverflowListMaxHeight = 400;

    private UIViewController? _overflowController;
    private readonly OverflowPopoverDelegate _overflowPopoverDelegate = new();

    private static void DrawMoreLinks(IReadOnlyList<CalendarOverflowCell> overflows)
    {
        if (overflows.Count == 0) return;
        var paragraph = new NSMutableParagraphStyle { LineBreakMode = UILineBreakMode.TailTruncation, Alignment = UITextAlignment.Left };
        var attributes = new UIStringAttributes { Font = WinoStyle.Fixed(11, UIFontWeight.Semibold), ForegroundColor = WinoStyle.Accent, ParagraphStyle = paragraph };
        foreach (var overflow in overflows)
        {
            var text = new NSAttributedString(overflow.Text, attributes);
            double height = text.Size.Height;
            var bounds = overflow.Bounds;
            text.DrawString(new CGRect(bounds.X + 4, bounds.Y + (bounds.Height - height) / 2, Math.Max(0, bounds.Width - 8), height));
        }
    }

    internal void ShowOverflow(UIView host, CalendarOverflowCell overflow)
    {
        CloseOverflow();
        int count = overflow.HiddenItems.Count;
        if (count == 0 || FindViewController() is not { } presenter) return;

        double width = OverflowListWidth + OverflowListPadding * 2;
        double contentHeight = OverflowListPadding * 2 + count * OverflowTileHeight + (count - 1) * OverflowTileGap;
        var list = new UIView { Frame = new CGRect(0, 0, width, contentHeight) };
        for (int index = 0; index < count; index++)
        {
            var item = overflow.HiddenItems[index];
            var tile = new WinoCalendarItemView
            {
                Item = item,
                Kind = CalendarPlacementKind.AllDay,
                Date = overflow.Date,
                Model = Tile(item, overflow.Date),
                Frame = new CGRect(OverflowListPadding, OverflowListPadding + index * (OverflowTileHeight + OverflowTileGap), OverflowListWidth, OverflowTileHeight),
                UserInteractionEnabled = true
            };
            tile.AddGestureRecognizer(new UITapGestureRecognizer(recognizer =>
            {
                CloseOverflow();
                if (recognizer.View is WinoCalendarItemView tapped) RaiseItem(tapped, recognizer, 1);
            }));
            list.AddSubview(tile);
        }

        var scroll = new UIScrollView
        {
            ContentSize = list.Frame.Size,
            AlwaysBounceVertical = false,
            ShowsHorizontalScrollIndicator = false,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions
        };
        scroll.AddSubview(list);

        var controller = new UIViewController
        {
            ModalPresentationStyle = UIModalPresentationStyle.Popover,
            PreferredContentSize = new CGSize(width, Math.Min(contentHeight, OverflowListMaxHeight))
        };
        scroll.Frame = controller.View!.Bounds;
        controller.View.AddSubview(scroll);
        controller.View.AccessibilityLabel = CalendarItemOverflow.FormatAccessibleName(count, overflow.Date, _settings?.CultureInfo);
        if (controller.PopoverPresentationController is { } popover)
        {
            popover.SourceView = host;
            popover.SourceRect = overflow.Bounds;
            popover.PermittedArrowDirections = UIPopoverArrowDirection.Any;
            popover.Delegate = _overflowPopoverDelegate;
        }

        _overflowController = controller;
        presenter.PresentViewController(controller, true, null);
    }

    private void CloseOverflow()
    {
        var controller = _overflowController;
        _overflowController = null;
        if (controller is { PresentingViewController: not null }) controller.DismissViewController(true, null);
    }

    private UIViewController? FindViewController()
    {
        UIResponder? responder = this;
        while (responder is not null)
        {
            if (responder is UIViewController controller) return controller;
            responder = responder.NextResponder;
        }
        return null;
    }

    /// <summary>Keeps the popover a popover on the iPhone too, where it would otherwise become a full sheet.</summary>
    private sealed class OverflowPopoverDelegate : UIPopoverPresentationControllerDelegate
    {
        public override UIModalPresentationStyle GetAdaptivePresentationStyle(UIPresentationController forPresentationController, UITraitCollection traitCollection)
            => UIModalPresentationStyle.None;
    }
}
