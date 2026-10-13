using AppKit;
using CoreGraphics;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.Controls.Core.Layout;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// A wrapping row of round colour swatches with a selection ring (Windows accent and account colour
/// GridViews). Colours are #RRGGBB strings so the picker binds straight to the shared ViewModels.
/// </summary>
public sealed class WinoColorSwatchPicker : NSView
{
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
        => new(NSView.NoIntrinsicMetric, (nfloat)WinoSwatchGridLayout.Height(_swatches.Count, Frame.Width > 0 ? Frame.Width : WinoSwatchGridLayout.DefaultWidth));

    public override void Layout()
    {
        base.Layout();
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

    /// <summary>A swatch with button behaviour (press tracking, Space/Return, focus ring) from <see cref="WinoPressableView"/>.</summary>
    private sealed class SwatchView : WinoPressableView
    {
        private readonly NSColor _color;
        private bool _isSelected;

        public SwatchView(string hex, NSColor color)
        {
            Hex = hex;
            _color = color;
            AccessibilityLabel = hex;
            ToolTip = hex;
        }

        public string Hex { get; }
        public event EventHandler? Pressed;

        // Drawn in DrawRect like WinoRoundCheckbox, not through the surface layer.
        public override bool WantsUpdateLayer => false;

        protected override void OnActivated() => Pressed?.Invoke(this, EventArgs.Empty);

        public override void DrawFocusRingMask() => NSBezierPath.FromOvalInRect(Bounds.Inset(1, 1)).Fill();

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; AccessibilitySelected = value; NeedsDisplay = true; }
        }

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
