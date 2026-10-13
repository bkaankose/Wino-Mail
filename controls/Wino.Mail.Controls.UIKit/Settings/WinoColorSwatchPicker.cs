using CoreGraphics;
using UIKit;
using Wino.Mail.Controls.Core.Layout;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// A wrapping grid of round colour swatches with a selection ring (Windows accent and account colour GridViews; UIKit
/// twin of the AppKit <c>WinoColorSwatchPicker</c>, shared <see cref="WinoSwatchGridLayout"/>). Colours are #RRGGBB
/// strings so the picker binds straight to the shared ViewModels. Swatches are 22pt drawn inside a larger touch area.
/// </summary>
public sealed class WinoColorSwatchPicker : UIView
{
    private readonly List<SwatchView> _swatches = new();
    private string? _selectedHex;
    private int _laidOutRows = -1;

    public WinoColorSwatchPicker()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AccessibilityTraits = UIAccessibilityTrait.None;
    }

    public event EventHandler<string>? SelectionChanged;

    public IReadOnlyList<string> Colors
    {
        get => _swatches.Select(swatch => swatch.Hex).ToList();
        set
        {
            foreach (var swatch in _swatches) swatch.RemoveFromSuperview();
            _swatches.Clear();
            foreach (var hex in value)
            {
                var swatch = new SwatchView(hex, WinoStyle.FromHexString(hex) ?? WinoStyle.Accent);
                swatch.Pressed += (sender, _) => { if (sender is SwatchView pressed) Select(pressed.Hex, notify: true); };
                swatch.TranslatesAutoresizingMaskIntoConstraints = true;
                _swatches.Add(swatch);
                AddSubview(swatch);
            }
            ApplySelection();
            InvalidateIntrinsicContentSize();
            SetNeedsLayout();
        }
    }

    public string? SelectedHex
    {
        get => _selectedHex;
        set => Select(value, notify: false);
    }

    private void Select(string? hex, bool notify)
    {
        _selectedHex = hex;
        ApplySelection();
        if (notify && hex is not null) SelectionChanged?.Invoke(this, hex);
    }

    private void ApplySelection()
    {
        foreach (var swatch in _swatches)
            swatch.IsSelected = string.Equals(swatch.Hex, _selectedHex, StringComparison.OrdinalIgnoreCase);
    }

    public override CGSize IntrinsicContentSize
        => new(NoIntrinsicMetric, (nfloat)WinoSwatchGridLayout.Height(_swatches.Count, Frame.Width > 0 ? Frame.Width : WinoSwatchGridLayout.DefaultWidth));

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var perRow = WinoSwatchGridLayout.Columns(Frame.Width);
        for (int index = 0; index < _swatches.Count; index++)
        {
            var frame = WinoSwatchGridLayout.Frame(index, perRow);
            _swatches[index].Frame = new CGRect(frame.X, frame.Y, frame.Width, frame.Height);
        }
        var rows = WinoSwatchGridLayout.Rows(_swatches.Count, perRow);
        if (rows != _laidOutRows)
        {
            _laidOutRows = rows;
            InvalidateIntrinsicContentSize();
        }
    }

    /// <summary>A swatch with button behaviour from <see cref="WinoPressableView"/>; it accepts touches a little outside its circle.</summary>
    private sealed class SwatchView : WinoPressableView
    {
        private readonly UIColor _color;
        private bool _isSelected;

        public SwatchView(string hex, UIColor color)
        {
            Hex = hex;
            _color = color;
            ContentMode = UIViewContentMode.Redraw;
            Opaque = false;
            AccessibilityLabel = hex;
        }

        public string Hex { get; }

        public event EventHandler? Pressed;

        protected override void OnActivated() => Pressed?.Invoke(this, EventArgs.Empty);

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                AccessibilityTraits = value ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
                SetNeedsDisplay();
            }
        }

        public override bool PointInside(CGPoint point, UIEvent? uievent) => Bounds.Inset(-4, -4).Contains(point);

        public override void Draw(CGRect rect)
        {
            var inset = _isSelected ? 4 : 1;
            _color.SetFill();
            UIBezierPath.FromOval(Bounds.Inset(inset, inset)).Fill();
            if (!_isSelected) return;
            WinoStyle.PrimaryText.ColorWithAlpha(0.85f).SetStroke();
            var ring = UIBezierPath.FromOval(Bounds.Inset(1, 1));
            ring.LineWidth = 1.5f;
            ring.Stroke();
        }
    }
}
