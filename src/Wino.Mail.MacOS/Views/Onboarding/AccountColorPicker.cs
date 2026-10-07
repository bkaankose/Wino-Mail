using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Account colour choice (Windows AccountColorGridView with its Clear color link): a "no colour"
/// swatch followed by the round colour swatches. Selection is a #RRGGBB string, null for no colour.
/// </summary>
internal sealed class AccountColorPicker : NSView
{
    private readonly NoColorSwatch _none = new();
    private readonly WinoColorSwatchPicker _swatches = new();

    public AccountColorPicker()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _none.Pressed += (_, _) => Cleared?.Invoke(this, EventArgs.Empty);
        _swatches.SelectionChanged += (_, hex) => ColorSelected?.Invoke(this, hex);
        AddSubview(_none);
        AddSubview(_swatches);
        NSLayoutConstraint.ActivateConstraints(
        [
            // The swatch picker insets its circles by 2pt; the no-colour swatch lines up with them.
            _none.TopAnchor.ConstraintEqualTo(TopAnchor, 2),
            _none.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 2),
            _none.BottomAnchor.ConstraintLessThanOrEqualTo(BottomAnchor),
            _swatches.TopAnchor.ConstraintEqualTo(TopAnchor),
            _swatches.LeadingAnchor.ConstraintEqualTo(_none.TrailingAnchor, 6),
            _swatches.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _swatches.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
        WinoAccessibility.Label(_swatches, Translator.AccountDetailsPage_ColorPicker_Title);
    }

    public event EventHandler<string>? ColorSelected;
    public event EventHandler? Cleared;

    public IReadOnlyList<string> Colors
    {
        get => _swatches.Colors;
        set => _swatches.Colors = value;
    }

    public string? SelectedHex
    {
        get => _swatches.SelectedHex;
        set
        {
            _swatches.SelectedHex = value;
            _none.IsSelected = string.IsNullOrEmpty(value);
        }
    }

    /// <summary>A round "no colour" swatch: an outlined circle with a diagonal stroke.</summary>
    private sealed class NoColorSwatch : NSView
    {
        private bool _isSelected;

        public NoColorSwatch()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            WinoLayout.Size(this, 22, 22);
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.ButtonRole;
            AccessibilityLabel = Translator.ProviderSelection_ClearColor;
            ToolTip = Translator.ProviderSelection_ClearColor;
        }

        public event EventHandler? Pressed;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; AccessibilitySelected = value; NeedsDisplay = true; }
        }

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;
        public override void MouseUp(NSEvent theEvent) => Pressed?.Invoke(this, EventArgs.Empty);
        public override bool AccessibilityPerformPress() { Pressed?.Invoke(this, EventArgs.Empty); return true; }
        public override void ViewDidChangeEffectiveAppearance() { base.ViewDidChangeEffectiveAppearance(); NeedsDisplay = true; }

        public override void DrawRect(CGRect dirtyRect)
        {
            var inset = _isSelected ? 4 : 1.5;
            var circle = Bounds.Inset((nfloat)inset, (nfloat)inset);
            NSColor.ControlBackground.SetFill();
            NSBezierPath.FromOvalInRect(circle).Fill();
            WinoStyle.SecondaryText.SetStroke();
            var outline = NSBezierPath.FromOvalInRect(circle);
            outline.LineWidth = 1;
            outline.Stroke();
            NSColor.SystemRed.SetStroke();
            var slash = new NSBezierPath { LineWidth = 1.5f };
            var d = circle.Width * 0.3535;
            slash.MoveTo(new CGPoint(circle.GetMidX() - d, circle.GetMidY() - d));
            slash.LineTo(new CGPoint(circle.GetMidX() + d, circle.GetMidY() + d));
            slash.Stroke();
            if (!_isSelected) return;
            WinoStyle.PrimaryText.ColorWithAlphaComponent(0.85f).SetStroke();
            var ring = NSBezierPath.FromOvalInRect(Bounds.Inset(1, 1));
            ring.LineWidth = 1.5f;
            ring.Stroke();
        }
    }
}

/// <summary>
/// The account summary's colour button (Windows ProviderSelectionAccountColor): a paint brush with
/// no colour, a filled dot with one, and a popover holding the swatches and Clear color.
/// </summary>
internal sealed class AccountColorButton : NSButton
{
    private readonly AccountColorPicker _picker = new();
    private readonly NSButton _clear;
    private NSPopover? _popover;
    private string? _hex;

    public AccountColorButton()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BezelStyle = NSBezelStyle.Rounded;
        Title = string.Empty;
        ImagePosition = NSCellImagePosition.ImageOnly;
        ToolTip = Translator.AccountDetailsPage_ColorPicker_Title;
        WinoAccessibility.Label(this, Translator.AccountDetailsPage_ColorPicker_Title);
        WinoAccessibility.Help(this, Translator.AccountDetailsPage_ColorPicker_Description);
        _picker.ColorSelected += (_, hex) => { ColorSelected?.Invoke(this, hex); _popover?.Close(); };
        _picker.Cleared += (_, _) => { Cleared?.Invoke(this, EventArgs.Empty); _popover?.Close(); };
        _clear = new NSButton { Title = Translator.ProviderSelection_ClearColor, BezelStyle = NSBezelStyle.Inline, Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false };
        _clear.AttributedTitle = new NSAttributedString(Translator.ProviderSelection_ClearColor, new NSStringAttributes { ForegroundColor = WinoStyle.Accent, Font = WinoStyle.Body });
        _clear.Activated += (_, _) => { Cleared?.Invoke(this, EventArgs.Empty); _popover?.Close(); };
        Activated += (_, _) => ShowPopover();
        UpdateImage();
    }

    public event EventHandler<string>? ColorSelected;
    public event EventHandler? Cleared;

    public IReadOnlyList<string> Colors
    {
        get => _picker.Colors;
        set => _picker.Colors = value;
    }

    public string? SelectedHex
    {
        get => _hex;
        set
        {
            _hex = value;
            _picker.SelectedHex = value;
            _clear.Hidden = string.IsNullOrEmpty(value);
            UpdateImage();
        }
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        UpdateImage();
    }

    private void UpdateImage()
    {
        var color = WinoStyle.FromHexString(_hex);
        Image = color is null
            ? WinoIcons.Image(WinoIconGlyph.PaintBrush, 16, WinoStyle.PrimaryText, Translator.AccountDetailsPage_ColorPicker_Title)
            : NSImage.ImageWithSize(new CGSize(16, 16), false, rect =>
            {
                color.SetFill();
                NSBezierPath.FromOvalInRect(rect.Inset(1, 1)).Fill();
                return true;
            });
    }

    private void ShowPopover()
    {
        _popover ??= BuildPopover();
        if (_popover.Shown) { _popover.Close(); return; }
        // Clear color only shows with a colour, so the popover re-measures before it opens.
        if (_popover.ContentViewController is { View: { } content } controller)
        {
            content.LayoutSubtreeIfNeeded();
            controller.PreferredContentSize = content.FittingSize;
        }
        _popover.Show(Bounds, this, NSRectEdge.MaxYEdge);
    }

    private NSPopover BuildPopover()
    {
        // Five swatches per row after the no-colour swatch, like the 150pt Windows flyout grid.
        const int perRow = 5;
        const double pickerWidth = 22 + 6 + perRow * 30 + 4;
        var rows = Math.Max(1, (int)Math.Ceiling(_picker.Colors.Count / (double)perRow));
        var pickerHeight = rows * 22 + (rows - 1) * 8 + 4;
        var title = WinoStyle.Label(Translator.AccountDetailsPage_ColorPicker_Title, WinoStyle.BodyStrong);
        WinoLayout.Size(_picker, pickerWidth, pickerHeight);
        var stack = WinoLayout.VStack(WinoStyle.Space2, title, _picker, _clear);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.EdgeInsets = new NSEdgeInsets(12, 14, 12, 14);
        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(stack, content);
        content.LayoutSubtreeIfNeeded();
        var controller = new NSViewController { View = content, PreferredContentSize = content.FittingSize };
        return new NSPopover { Behavior = NSPopoverBehavior.Transient, ContentViewController = controller, Animates = true };
    }
}
