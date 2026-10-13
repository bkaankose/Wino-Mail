using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>Coverage band colours shared by the histogram bars and the band tiles.</summary>
internal static class CoverageColors
{
    public static UIColor Indexed => WinoStyle.Dynamic(WinoStyle.Hex(0x0F7B0F), WinoStyle.Hex(0x6CCB5F));
    public static UIColor ToIndex => WinoStyle.Accent;
    public static UIColor Outside => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.22), WinoStyle.Hex(0xFFFFFF, 0.24));
}

/// <summary>
/// The coverage histogram (Windows CoverageBucketTemplate in an ItemsRepeater): one column per bucket
/// in the ViewModel's order, left to right, each stacking indexed (bottom), will be indexed, then left out.
/// </summary>
internal sealed class CoverageHistogramView : UIView
{
    private IReadOnlyList<SemanticIndexRangeBucketViewModel> _buckets = [];

    public CoverageHistogramView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        ContentMode = UIViewContentMode.Redraw;
        HeightAnchor.ConstraintEqualTo((nfloat)SemanticIndexRangeBucketViewModel.MaximumBarHeight).Active = true;
        IsAccessibilityElement = false;
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
    }

    public void SetBuckets(IReadOnlyList<SemanticIndexRangeBucketViewModel> buckets)
    {
        _buckets = buckets;
        SetNeedsDisplay();
    }

    public override void Draw(CGRect rect)
    {
        if (_buckets.Count == 0) return;
        const double gap = 2;
        var width = Math.Max(1, (Bounds.Width - gap * (_buckets.Count - 1)) / _buckets.Count);
        for (int i = 0; i < _buckets.Count; i++)
        {
            var bucket = _buckets[i];
            var x = i * (width + gap);
            // UIKit draws top-down, so segments grow up from the bottom edge.
            double y = Bounds.Height;
            void Segment(double height, UIColor color, bool top)
            {
                if (height <= 0) return;
                color.SetFill();
                y -= height;
                var bar = new CGRect(x, y, width, height);
                if (top) UIBezierPath.FromRoundedRect(bar, 1).Fill(); else UIBezierPath.FromRect(bar).Fill();
            }
            Segment(bucket.IndexedHeight, CoverageColors.Indexed, bucket.SelectedNotIndexedHeight <= 0 && bucket.OutsideHeight <= 0);
            Segment(bucket.SelectedNotIndexedHeight, CoverageColors.ToIndex, bucket.OutsideHeight <= 0);
            Segment(bucket.OutsideHeight, CoverageColors.Outside, true);
        }
    }
}

/// <summary>
/// The Windows RangeSelector: a track with two thumbs selecting [start, end] in 0…maximum. Raises
/// <see cref="Changed"/> while dragging (the thumb nearest the touch moves) or adjusting from VoiceOver, where each
/// thumb is its own adjustable element stepping 1% of the folder.
/// </summary>
internal sealed class CoverageRangeView : UIControl
{
    private const double Thumb = 28;
    private double _maximum = 1, _start, _end;
    private int _dragging = -1;
    private readonly ThumbElement[] _thumbs;

    public CoverageRangeView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        ContentMode = UIViewContentMode.Redraw;
        HeightAnchor.ConstraintEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        IsAccessibilityElement = false;
        _thumbs = [new ThumbElement(this, 0, Translator.SearchBar_DateStart), new ThumbElement(this, 1, Translator.SearchBar_DateEnd)];
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
    }

    /// <summary>The two thumbs are the view's accessibility children.</summary>
    [Export("accessibilityElements")]
    public NSArray ThumbElements => NSArray.FromNSObjects(_thumbs);

    public double Maximum { get => _maximum; set { _maximum = Math.Max(1, value); SetNeedsDisplay(); } }
    public double Start { get => _start; set { _start = Math.Clamp(value, 0, _maximum); SetNeedsDisplay(); } }
    public double End { get => _end; set { _end = Math.Clamp(value, 0, _maximum); SetNeedsDisplay(); } }
    public event EventHandler? Changed;

    private double Track => Math.Max(1, Bounds.Width - Thumb);
    private double XFor(double value) => Thumb / 2 + value / _maximum * Track;
    private double ValueFor(double x) => Math.Clamp((x - Thumb / 2) / Track * _maximum, 0, _maximum);
    private double Value(int thumb) => thumb == 0 ? _start : _end;
    private CGRect ThumbRect(int thumb) => new(XFor(Value(thumb)) - Thumb / 2, Bounds.GetMidY() - Thumb / 2, Thumb, Thumb);
    // One accessibility step is 1% of the folder, at least one message.
    private double Step => Math.Max(1, Math.Round(_maximum / 100));

    /// <summary>Moves one thumb, keeping start ≤ end, and raises <see cref="Changed"/> when it moved.</summary>
    private void Move(int thumb, double value)
    {
        value = Math.Round(Math.Clamp(value, 0, _maximum));
        value = thumb == 0 ? Math.Min(value, _end) : Math.Max(value, _start);
        if (value == Value(thumb)) return;
        if (thumb == 0) Start = value; else End = value;
        Changed?.Invoke(this, EventArgs.Empty);
        SendActionForControlEvents(UIControlEvent.ValueChanged);
    }

    public override void Draw(CGRect rect)
    {
        var mid = Bounds.GetMidY();
        WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.12), WinoStyle.Hex(0xFFFFFF, 0.16)).SetFill();
        UIBezierPath.FromRoundedRect(new CGRect(Thumb / 2, mid - 2, Track, 4), 2).Fill();
        WinoStyle.Accent.SetFill();
        UIBezierPath.FromRoundedRect(new CGRect(XFor(_start), mid - 2, Math.Max(0, XFor(_end) - XFor(_start)), 4), 2).Fill();
        for (int thumb = 0; thumb < 2; thumb++)
        {
            var path = UIBezierPath.FromOval(ThumbRect(thumb).Inset(0.5f, 0.5f));
            WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x5A5A60)).SetFill();
            path.Fill();
            WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.2), WinoStyle.Hex(0xFFFFFF, 0.2)).SetStroke();
            path.Stroke();
        }
    }

    public override bool BeginTracking(UITouch uitouch, UIEvent? uievent)
    {
        var x = uitouch.LocationInView(this).X;
        _dragging = Math.Abs(x - XFor(_start)) <= Math.Abs(x - XFor(_end)) ? 0 : 1;
        // Overlapping thumbs at the end of the track: the start thumb is the one that can move.
        if (_start == _end && _end >= _maximum) _dragging = 0;
        Move(_dragging, ValueFor(x));
        return true;
    }

    public override bool ContinueTracking(UITouch uitouch, UIEvent? uievent)
    {
        if (_dragging >= 0) Move(_dragging, ValueFor(uitouch.LocationInView(this).X));
        return true;
    }

    public override void EndTracking(UITouch? uitouch, UIEvent? uievent) => _dragging = -1;
    public override void CancelTracking(UIEvent? uievent) => _dragging = -1;

    /// <summary>One thumb as an adjustable accessibility element: its value and stepping.</summary>
    private sealed class ThumbElement : UIAccessibilityElement
    {
        private readonly CoverageRangeView _owner;
        private readonly int _thumb;

        public ThumbElement(CoverageRangeView owner, int thumb, string label) : base(owner)
        {
            _owner = owner;
            _thumb = thumb;
            AccessibilityLabel = label;
            AccessibilityTraits = (ulong)UIAccessibilityTrait.Adjustable;
        }

        public override string? AccessibilityValue
        {
            get => _owner.Value(_thumb).ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            set { }
        }

        public override CGRect AccessibilityFrameInContainerSpace
        {
            get => _owner.ThumbRect(_thumb).Inset(-8, -8);
            set { }
        }

        public override void AccessibilityIncrement() => _owner.Move(_thumb, _owner.Value(_thumb) + _owner.Step);
        public override void AccessibilityDecrement() => _owner.Move(_thumb, _owner.Value(_thumb) - _owner.Step);
    }
}

/// <summary>
/// One row of the folder tree (the Mac outline cell and row view): indent by depth, glyph, name (bold when selected),
/// included dot and message count, with the pane selection fill and the 3pt accent bar. Tapping selects the folder.
/// </summary>
internal sealed class CoverageFolderCell : UIControl
{
    private const double IndentPerLevel = 14;

    private readonly WinoSurfaceView _selection = new() { Fill = WinoSettingsStyle.SelectedFill, CornerRadius = 5 };
    private readonly WinoSurfaceView _bar = new() { Fill = WinoStyle.Accent, CornerRadius = 1.5 };
    private readonly UILabel _name = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText);
    private readonly WinoSurfaceView _dot = new() { Fill = WinoStyle.Accent, CornerRadius = 3.5 };
    private readonly UILabel _count = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private bool _isCurrent;

    public CoverageFolderCell(IntelligenceFolderNode node, int depth)
    {
        Node = node;
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;

        WinoLayout.Fill(_selection, this, 1, 4, 1, 4);
        _selection.UserInteractionEnabled = false;
        _bar.TranslatesAutoresizingMaskIntoConstraints = false;
        _selection.AddSubview(_bar);
        NSLayoutConstraint.ActivateConstraints(
        [
            _bar.LeadingAnchor.ConstraintEqualTo(_selection.LeadingAnchor),
            _bar.CenterYAnchor.ConstraintEqualTo(_selection.CenterYAnchor),
            _bar.WidthAnchor.ConstraintEqualTo(3),
            _bar.HeightAnchor.ConstraintEqualTo(16)
        ]);

        var icon = new WinoIconView(WinoIconGlyph.Folder, 15);
        WinoLayout.Size(icon, 15, 15);
        _name.LineBreakMode = UILineBreakMode.TailTruncation;
        WinoLayout.Priorities(_name, UILayoutConstraintAxis.Horizontal, 1, 250);
        WinoLayout.Size(_dot, 7, 7);
        _count.TextAlignment = UITextAlignment.Right;
        _count.WidthAnchor.ConstraintGreaterThanOrEqualTo(38).Active = true;
        WinoLayout.Priorities(_count, UILayoutConstraintAxis.Horizontal, 750, 1000);
        var row = WinoLayout.HStack(8, icon, _name, _dot, _count);
        row.UserInteractionEnabled = false;
        WinoLayout.Fill(row, this, 0, 16 + depth * IndentPerLevel, 0, 16);

        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        AddTarget((_, _) => Picked?.Invoke(this, EventArgs.Empty), UIControlEvent.TouchUpInside);
    }

    public IntelligenceFolderNode Node { get; }

    /// <summary>Raised when the user taps the row.</summary>
    public event EventHandler? Picked;

    public override bool Highlighted
    {
        get => base.Highlighted;
        set { base.Highlighted = value; UpdateSelection(); }
    }

    public void Configure(bool current)
    {
        _isCurrent = current;
        _name.Text = Node.DisplayName;
        _name.Font = current ? WinoStyle.BodyStrong : WinoStyle.Body;
        _dot.Hidden = !Node.IsIncluded;
        _count.Text = Node.AvailableMessageCountText;
        AccessibilityLabel = Node.RowAutomationName;
        AccessibilityHint = Node.CoverageBadgeTooltip;
        AccessibilityTraits = current ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        _selection.Hidden = !_isCurrent && !Highlighted;
        _bar.Hidden = !_isCurrent;
    }
}

/// <summary>Wrapping row of items (Windows WrapPanel): lays items out left to right and wraps to the width.</summary>
internal sealed class WrapView : UIView
{
    private readonly double _spacing;
    private readonly double _minimumItemWidth;
    private double _height;

    public WrapView(double spacing, double minimumItemWidth = 0)
    {
        _spacing = spacing;
        _minimumItemWidth = minimumItemWidth;
        TranslatesAutoresizingMaskIntoConstraints = false;
    }

    public void AddItem(UIView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = true;
        AddSubview(view);
        SetNeedsLayout();
        InvalidateIntrinsicContentSize();
    }

    public void Clear()
    {
        foreach (var view in Subviews) view.RemoveFromSuperview();
        SetNeedsLayout();
        InvalidateIntrinsicContentSize();
    }

    public override CGSize IntrinsicContentSize => new(UIView.NoIntrinsicMetric, Math.Max(_height, 28));

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        double x = 0, y = 0, line = 0;
        var available = Math.Max(1, (double)Bounds.Width);
        foreach (var view in Subviews)
        {
            var size = view.SystemLayoutSizeFittingSize(UIView.UILayoutFittingCompressedSize);
            var width = Math.Min(available, Math.Max(size.Width, _minimumItemWidth));
            if (x > 0 && x + width > available) { x = 0; y += line + _spacing; line = 0; }
            view.Frame = new CGRect(x, y, width, size.Height);
            x += width + _spacing;
            line = Math.Max(line, size.Height);
        }
        var height = y + line;
        if (Math.Abs(height - _height) > 0.5) { _height = height; InvalidateIntrinsicContentSize(); }
    }
}
