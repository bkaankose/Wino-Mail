using AppKit;
using CoreGraphics;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Shell;

/// <summary>
/// Row background for the shell pane. Selection is the Windows NavigationViewItem look: a subtle
/// rounded fill inset from the pane edges and a 3pt accent pipe on the left, never the blue
/// source-list highlight. <see cref="ShowsIndicator"/> draws the pipe alone, which is how the
/// active account is marked while a folder below it holds the selection.
/// </summary>
public sealed class WinoShellRowView : NSTableRowView
{
    public const double HorizontalInset = 6;
    public const double CornerRadius = 5;

    private bool _showsIndicator;
    private bool _highlightable = true;
    private bool _isDropTarget;

    /// <summary>Draws the accent pipe even while the row is not selected.</summary>
    public bool ShowsIndicator
    {
        get => _showsIndicator;
        set { if (_showsIndicator == value) return; _showsIndicator = value; NeedsDisplay = true; }
    }

    /// <summary>False for rows that never show a selection fill (headers, separators, hosted controls).</summary>
    public bool Highlightable
    {
        get => _highlightable;
        set { if (_highlightable == value) return; _highlightable = value; NeedsDisplay = true; }
    }

    /// <summary>
    /// Marks the row as the target of a drag in progress: an accent fill at 16% with a 2pt inset accent
    /// ring (the Windows IsDraggingItemOver state), drawn instead of the native blue drop outline.
    /// </summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget == value) return; _isDropTarget = value; NeedsDisplay = true; }
    }

    /// <summary>Cells keep their own colours; nothing is inverted on selection.</summary>
    public override NSBackgroundStyle InteriorBackgroundStyle => NSBackgroundStyle.Normal;

    public override void DrawSelection(CGRect dirtyRect)
    {
        if (!_highlightable) return;
        var rect = new CGRect(Bounds.X + HorizontalInset, Bounds.Y + 0.5, Bounds.Width - HorizontalInset * 2, Bounds.Height - 1);
        WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10)).SetFill();
        NSBezierPath.FromRoundedRect(rect, (nfloat)CornerRadius, (nfloat)CornerRadius).Fill();
        DrawIndicator();
    }

    public override void DrawBackground(CGRect dirtyRect)
    {
        base.DrawBackground(dirtyRect);
        if (!Selected && _showsIndicator) DrawIndicator();
        if (_isDropTarget) DrawDropTarget();
    }

    private void DrawDropTarget()
    {
        var rect = new CGRect(Bounds.X + HorizontalInset, Bounds.Y + 0.5, Bounds.Width - HorizontalInset * 2, Bounds.Height - 1);
        WinoStyle.Accent.ColorWithAlphaComponent(0.16f).SetFill();
        NSBezierPath.FromRoundedRect(rect, (nfloat)CornerRadius, (nfloat)CornerRadius).Fill();
        var ring = NSBezierPath.FromRoundedRect(rect.Inset(1, 1), (nfloat)(CornerRadius - 1), (nfloat)(CornerRadius - 1));
        ring.LineWidth = 2;
        WinoStyle.Accent.SetStroke();
        ring.Stroke();
    }

    /// <summary>The pane draws its own drop highlight (<see cref="IsDropTarget"/>).</summary>
    public override void DrawDraggingDestinationFeedback(CGRect dirtyRect) { }

    private void DrawIndicator()
    {
        var inset = Math.Min(11, Bounds.Height / 4);
        var rect = new CGRect(Bounds.X + 4, Bounds.Y + inset, 3, Bounds.Height - inset * 2);
        WinoStyle.Accent.SetFill();
        NSBezierPath.FromRoundedRect(rect, 1.5f, 1.5f).Fill();
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }
}
