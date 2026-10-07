using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Wino.Presentation.AppKit;

/// <summary>
/// Native NSButton checkboxes for Wino. Since macOS 26 the unchecked Aqua box is a light gray fill
/// without an outline, which nearly disappears on white cards and light window backgrounds. The
/// cell keeps the system drawing (state, accent, focus, dark mode) and adds a hairline outline in
/// the dynamic tertiary label color while the box is unchecked.
/// </summary>
public static class WinoCheckbox
{
    public static NSButton Create(string? title, Action? activated = null)
    {
        var button = new NSButton { Cell = new WinoCheckboxCell(), TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetButtonType(NSButtonType.Switch);
        button.Title = title ?? string.Empty;
        if (activated is not null) button.Activated += (_, _) => activated();
        return button;
    }
}

[Register(nameof(WinoCheckboxCell))]
public sealed class WinoCheckboxCell : NSButtonCell
{
    public WinoCheckboxCell() { }
    public WinoCheckboxCell(NativeHandle handle) : base(handle) { }

    public override void DrawImage(NSImage image, CGRect frame, NSView controlView)
    {
        base.DrawImage(image, frame, controlView);
        if (State != NSCellStateValue.Off) return;

        // The checkbox glyph is a square one point inside the image frame (16pt regular, 14pt small).
        nfloat side = (nfloat)Math.Min((double)frame.Width, (double)frame.Height) - 2;
        if (side <= 4) return;
        var box = new CGRect(frame.GetMidX() - side / 2, frame.GetMidY() - side / 2, side, side).Inset(0.5f, 0.5f);
        nfloat radius = side / 4;
        var stroke = Enabled ? NSColor.TertiaryLabel : NSColor.QuaternaryLabel;
        stroke.SetStroke();
        var path = NSBezierPath.FromRoundedRect(box, radius, radius);
        path.LineWidth = 1;
        path.Stroke();
    }
}
