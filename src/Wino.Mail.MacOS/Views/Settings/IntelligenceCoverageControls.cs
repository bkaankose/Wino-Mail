using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Coverage band colours shared by the histogram bars and the band tiles.</summary>
internal static class CoverageColors
{
    public static NSColor Indexed => WinoStyle.Dynamic(WinoStyle.Hex(0x0F7B0F), WinoStyle.Hex(0x6CCB5F));
    public static NSColor ToIndex => WinoStyle.Accent;
    public static NSColor Outside => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.22), WinoStyle.Hex(0xFFFFFF, 0.24));
}

/// <summary>
/// The coverage histogram (Windows CoverageBucketTemplate in an ItemsRepeater): one column per bucket
/// in the ViewModel's order, left to right, each stacking indexed (bottom), will be indexed, then left out.
/// </summary>
internal sealed class CoverageHistogramView : NSView
{
    private IReadOnlyList<SemanticIndexRangeBucketViewModel> _buckets = [];

    public CoverageHistogramView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintEqualTo((nfloat)SemanticIndexRangeBucketViewModel.MaximumBarHeight).Active = true;
        AccessibilityElement = false;
    }

    public override bool IsFlipped => false;

    public void SetBuckets(IReadOnlyList<SemanticIndexRangeBucketViewModel> buckets)
    {
        _buckets = buckets;
        NeedsDisplay = true;
    }

    public override void ViewDidChangeEffectiveAppearance() { base.ViewDidChangeEffectiveAppearance(); NeedsDisplay = true; }

    public override void DrawRect(CGRect dirtyRect)
    {
        if (_buckets.Count == 0) return;
        const double gap = 2;
        var width = Math.Max(1, (Bounds.Width - gap * (_buckets.Count - 1)) / _buckets.Count);
        for (int i = 0; i < _buckets.Count; i++)
        {
            var bucket = _buckets[i];
            var x = i * (width + gap);
            double y = 0;
            void Segment(double height, NSColor color, bool top)
            {
                if (height <= 0) return;
                color.SetFill();
                var rect = new CGRect(x, y, width, height);
                if (top) NSBezierPath.FromRoundedRect(rect, 1, 1).Fill(); else NSBezierPath.FillRect(rect);
                y += height;
            }
            Segment(bucket.IndexedHeight, CoverageColors.Indexed, bucket.SelectedNotIndexedHeight <= 0 && bucket.OutsideHeight <= 0);
            Segment(bucket.SelectedNotIndexedHeight, CoverageColors.ToIndex, bucket.OutsideHeight <= 0);
            Segment(bucket.OutsideHeight, CoverageColors.Outside, true);
        }
    }

    /// <summary>The bucket under a point, for the tooltip.</summary>
    public SemanticIndexRangeBucketViewModel? BucketAt(CGPoint point)
    {
        if (_buckets.Count == 0 || Bounds.Width <= 0) return null;
        var index = (int)(point.X / Bounds.Width * _buckets.Count);
        return index >= 0 && index < _buckets.Count ? _buckets[index] : null;
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        foreach (var area in TrackingAreas()) RemoveTrackingArea(area);
        AddTrackingArea(new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseMoved | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null));
    }

    public override void MouseMoved(NSEvent theEvent)
        => ToolTip = BucketAt(ConvertPointFromView(theEvent.LocationInWindow, null))?.Tooltip;
}

/// <summary>
/// The Windows RangeSelector: a track with two thumbs selecting [start, end] in 0…maximum. Raises
/// <see cref="Changed"/> while dragging or stepping from the keyboard. The active thumb (the one last
/// dragged, or switched with Tab / Shift-Tab) takes the arrows, Home and End and carries the focus ring;
/// each thumb is its own accessibility slider.
/// </summary>
internal sealed class CoverageRangeView : NSView
{
    private const double Thumb = 18;
    private double _maximum = 1, _start, _end;
    private int _dragging = -1, _active;
    private readonly ThumbElement[] _thumbs;

    public CoverageRangeView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintEqualTo(22).Active = true;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.GroupRole;
        _thumbs = [new ThumbElement(this, 0, Translator.SearchBar_DateStart), new ThumbElement(this, 1, Translator.SearchBar_DateEnd)];
        AccessibilityChildren = _thumbs;
    }

    public double Maximum { get => _maximum; set { _maximum = Math.Max(1, value); Invalidate(); } }
    public double Start { get => _start; set { _start = Math.Clamp(value, 0, _maximum); Invalidate(); } }
    public double End { get => _end; set { _end = Math.Clamp(value, 0, _maximum); Invalidate(); } }
    public event EventHandler? Changed;

    private double Track => Math.Max(1, Bounds.Width - Thumb);
    private double XFor(double value) => Thumb / 2 + value / _maximum * Track;
    private double ValueFor(double x) => Math.Clamp((x - Thumb / 2) / Track * _maximum, 0, _maximum);
    private double Value(int thumb) => thumb == 0 ? _start : _end;
    private CGRect ThumbRect(int thumb) => new(XFor(Value(thumb)) - Thumb / 2, Bounds.GetMidY() - Thumb / 2, Thumb, Thumb);
    // One keyboard step is 1% of the folder, at least one message.
    private double Step => Math.Max(1, Math.Round(_maximum / 100));

    private void Invalidate()
    {
        NeedsDisplay = true;
        NoteFocusRingMaskChanged();
    }

    /// <summary>Moves one thumb, keeping start ≤ end, and raises <see cref="Changed"/> when it moved.</summary>
    private void Move(int thumb, double value)
    {
        value = Math.Round(Math.Clamp(value, 0, _maximum));
        value = thumb == 0 ? Math.Min(value, _end) : Math.Max(value, _start);
        if (value == Value(thumb)) return;
        if (thumb == 0) Start = value; else End = value;
        Changed?.Invoke(this, EventArgs.Empty);
        NSAccessibility.PostNotification(_thumbs[thumb], new NSString("AXValueChanged"));
    }

    public override void ViewDidChangeEffectiveAppearance() { base.ViewDidChangeEffectiveAppearance(); NeedsDisplay = true; }

    public override void DrawRect(CGRect dirtyRect)
    {
        var mid = Bounds.GetMidY();
        WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.12), WinoStyle.Hex(0xFFFFFF, 0.16)).SetFill();
        NSBezierPath.FromRoundedRect(new CGRect(Thumb / 2, mid - 2, Track, 4), 2, 2).Fill();
        WinoStyle.Accent.SetFill();
        NSBezierPath.FromRoundedRect(new CGRect(XFor(_start), mid - 2, Math.Max(0, XFor(_end) - XFor(_start)), 4), 2, 2).Fill();
        for (int thumb = 0; thumb < 2; thumb++)
        {
            var path = NSBezierPath.FromOvalInRect(ThumbRect(thumb).Inset(0.5f, 0.5f));
            WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0x5A5A60)).SetFill();
            path.Fill();
            WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.2), WinoStyle.Hex(0xFFFFFF, 0.2)).SetStroke();
            path.Stroke();
        }
    }

    // The standard focus ring, which AppKit draws only while this view is first responder, circles the active thumb.
    public override bool AcceptsFirstResponder() => true;
    public override CGRect FocusRingMaskBounds => ThumbRect(_active);
    public override void DrawFocusRingMask() => NSBezierPath.FromOvalInRect(ThumbRect(_active)).Fill();

    public override bool BecomeFirstResponder()
    {
        // Arriving backwards through the key loop lands on the end thumb.
        var direction = Window?.KeyViewSelectionDirection() ?? NSSelectionDirection.Direct;
        if (direction != NSSelectionDirection.Direct) SetActive(direction == NSSelectionDirection.Previous ? 1 : 0);
        return base.BecomeFirstResponder();
    }

    private void SetActive(int thumb)
    {
        if (_active == thumb) return;
        _active = thumb;
        NoteFocusRingMaskChanged();
    }

    private static bool IsBackTab(NSEvent theEvent)
        => theEvent.Characters == "\u0019" || (theEvent.CharactersIgnoringModifiers == "\t" && theEvent.ModifierFlags.HasFlag(NSEventModifierMask.ShiftKeyMask));

    public override void KeyDown(NSEvent theEvent)
    {
        var key = theEvent.CharactersIgnoringModifiers is { Length: > 0 } characters ? characters[0] : '\0';
        var back = IsBackTab(theEvent);
        switch (key)
        {
            // Tab moves start → end and Shift-Tab end → start; past either edge the window's key loop takes over.
            case '\t' or '\u0019' when back ? _active == 1 : _active == 0: SetActive(back ? 0 : 1); return;
            case '\t' or '\u0019': if (back) Window?.SelectKeyViewPrecedingView(this); else Window?.SelectKeyViewFollowingView(this); return;
            case (char)NSFunctionKey.LeftArrow or (char)NSFunctionKey.DownArrow: Move(_active, Value(_active) - Step); return;
            case (char)NSFunctionKey.RightArrow or (char)NSFunctionKey.UpArrow: Move(_active, Value(_active) + Step); return;
            case (char)NSFunctionKey.Home: Move(_active, 0); return;
            case (char)NSFunctionKey.End: Move(_active, _maximum); return;
        }
        base.KeyDown(theEvent);
    }

    public override void MouseDown(NSEvent theEvent)
    {
        var x = ConvertPointFromView(theEvent.LocationInWindow, null).X;
        _dragging = Math.Abs(x - XFor(_start)) <= Math.Abs(x - XFor(_end)) ? 0 : 1;
        SetActive(_dragging);
        MouseDragged(theEvent);
    }

    public override void MouseDragged(NSEvent theEvent)
    {
        if (_dragging >= 0) Move(_dragging, ValueFor(ConvertPointFromView(theEvent.LocationInWindow, null).X));
    }

    public override void MouseUp(NSEvent theEvent) => _dragging = -1;

    /// <summary>One thumb as an accessibility slider: its value, limits up to the other thumb, and stepping.</summary>
    private sealed class ThumbElement : NSAccessibilityElement
    {
        private readonly CoverageRangeView _owner;
        private readonly int _thumb;

        public ThumbElement(CoverageRangeView owner, int thumb, string label)
        {
            _owner = owner;
            _thumb = thumb;
            AccessibilityRole = NSAccessibilityRoles.SliderRole;
            AccessibilityLabel = label;
            AccessibilityParent = owner;
        }

        public override NSObject AccessibilityValue { get => NSNumber.FromDouble(_owner.Value(_thumb)); set { } }
        public override NSObject AccessibilityMinValue { get => NSNumber.FromDouble(_thumb == 0 ? 0 : _owner._start); set { } }
        public override NSObject AccessibilityMaxValue { get => NSNumber.FromDouble(_thumb == 0 ? _owner._end : _owner._maximum); set { } }
        public override CGRect AccessibilityFrameInParentSpace { get => _owner.ThumbRect(_thumb); set { } }
        public override bool AccessibilityFocused { get => _owner.Window?.FirstResponder == _owner && _owner._active == _thumb; set { } }
        public override bool AccessibilityPerformIncrement() { _owner.Move(_thumb, _owner.Value(_thumb) + _owner.Step); return true; }
        public override bool AccessibilityPerformDecrement() { _owner.Move(_thumb, _owner.Value(_thumb) - _owner.Step); return true; }
    }
}

/// <summary>Reusable folder tree cell: glyph, name (bold when selected), included dot, message count.</summary>
internal sealed class CoverageFolderCell : NSTableCellView
{
    public const string ReuseIdentifier = "CoverageFolderCell";

    private readonly NSTextField _name = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText);
    private readonly WinoSurfaceView _dot = new() { Fill = WinoStyle.Accent, CornerRadius = 3.5 };
    private readonly NSTextField _count = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);

    public CoverageFolderCell()
    {
        Identifier = ReuseIdentifier;
        var icon = new WinoIconView(WinoIconGlyph.Folder, 15);
        WinoLayout.Size(icon, 15, 15);
        _name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _name.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        WinoLayout.Size(_dot, 7, 7);
        _count.Alignment = NSTextAlignment.Right;
        _count.WidthAnchor.ConstraintGreaterThanOrEqualTo(38).Active = true;
        var row = WinoLayout.HStack(8, icon, _name, WinoLayout.Spacer(), _dot, _count);
        row.EdgeInsets = new NSEdgeInsets(0, 4, 0, 12);
        WinoLayout.Fill(row, this);
    }

    public void Configure(IntelligenceFolderNode node, bool selected)
    {
        _name.StringValue = node.DisplayName;
        _name.Font = selected ? WinoStyle.BodyStrong : WinoStyle.Body;
        _dot.Hidden = !node.IsIncluded;
        _dot.ToolTip = node.CoverageBadgeTooltip;
        _count.StringValue = node.AvailableMessageCountText;
        AccessibilityLabel = node.RowAutomationName;
    }
}

/// <summary>Row view for the folder tree: the pane selection fill with the 3pt accent bar.</summary>
internal sealed class CoverageFolderRowView : NSTableRowView
{
    public const string ReuseIdentifier = "CoverageFolderRow";

    public CoverageFolderRowView() => Identifier = ReuseIdentifier;

    public override void DrawSelection(CGRect dirtyRect)
    {
        var rect = Bounds.Inset(4, 1);
        Wino.Mail.Controls.AppKit.Settings.WinoSettingsStyle.SelectedFill.SetFill();
        NSBezierPath.FromRoundedRect(rect, 5, 5).Fill();
        WinoStyle.Accent.SetFill();
        NSBezierPath.FromRoundedRect(new CGRect(rect.X, rect.GetMidY() - 8, 3, 16), 1.5f, 1.5f).Fill();
    }

    public override NSBackgroundStyle InteriorBackgroundStyle => NSBackgroundStyle.Normal;
}
