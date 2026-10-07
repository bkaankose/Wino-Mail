using AppKit;
using CoreGraphics;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// A wrapping row of round colour swatches with a selection ring (Windows accent and account colour
/// GridViews). Colours are #RRGGBB strings so the picker binds straight to the shared ViewModels.
/// </summary>
public sealed class WinoColorSwatchPicker : NSView
{
    private const double SwatchSize = 22;
    private const double Spacing = 8;
    private readonly List<SwatchView> _swatches = new();
    private string? _selectedHex;
    private int _laidOutRows = -1;

    public WinoColorSwatchPicker()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
    }

    public event EventHandler<string>? SelectionChanged;

    public override bool IsFlipped => true;

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
                swatch.Pressed += (_, _) => Select(swatch.Hex, notify: true);
                _swatches.Add(swatch);
                AddSubview(swatch);
            }
            ApplySelection();
            InvalidateIntrinsicContentSize();
            NeedsLayout = true;
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
    {
        get
        {
            var width = Frame.Width > 0 ? Frame.Width : 9 * (SwatchSize + Spacing);
            var perRow = Math.Max(1, (int)((width + Spacing) / (SwatchSize + Spacing)));
            var rows = Math.Max(1, (int)Math.Ceiling(_swatches.Count / (double)perRow));
            return new CGSize(NSView.NoIntrinsicMetric, (nfloat)(rows * SwatchSize + (rows - 1) * Spacing + 4));
        }
    }

    public override void Layout()
    {
        base.Layout();
        var width = Math.Max(SwatchSize, Frame.Width);
        var perRow = Math.Max(1, (int)((width + Spacing) / (SwatchSize + Spacing)));
        for (int index = 0; index < _swatches.Count; index++)
        {
            int row = index / perRow, column = index % perRow;
            _swatches[index].Frame = new CGRect(column * (SwatchSize + Spacing) + 2, row * (SwatchSize + Spacing) + 2, SwatchSize, SwatchSize);
        }
        var rows = Math.Max(1, (int)Math.Ceiling(_swatches.Count / (double)perRow));
        if (rows != _laidOutRows)
        {
            _laidOutRows = rows;
            InvalidateIntrinsicContentSize();
        }
    }

    private sealed class SwatchView : NSView
    {
        private readonly NSColor _color;
        private bool _isSelected;

        public SwatchView(string hex, NSColor color)
        {
            Hex = hex;
            _color = color;
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.ButtonRole;
            AccessibilityLabel = hex;
            ToolTip = hex;
        }

        public string Hex { get; }
        public event EventHandler? Pressed;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; AccessibilitySelected = value; NeedsDisplay = true; }
        }

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;
        public override void MouseUp(NSEvent theEvent) => Pressed?.Invoke(this, EventArgs.Empty);
        public override bool AccessibilityPerformPress() { Pressed?.Invoke(this, EventArgs.Empty); return true; }

        public override void DrawRect(CGRect dirtyRect)
        {
            var inset = _isSelected ? 4 : 1;
            _color.SetFill();
            NSBezierPath.FromOvalInRect(Bounds.Inset(inset, inset)).Fill();
            if (!_isSelected) return;
            WinoStyle.PrimaryText.ColorWithAlphaComponent(0.85f).SetStroke();
            var ring = NSBezierPath.FromOvalInRect(Bounds.Inset(1, 1));
            ring.LineWidth = 1.5f;
            ring.Stroke();
        }
    }
}
