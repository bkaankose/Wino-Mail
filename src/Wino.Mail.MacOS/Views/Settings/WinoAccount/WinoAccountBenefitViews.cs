using System.Globalization;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// One selectable offer (Windows BenefitTileTemplate): the glyph in a 36pt rounded box, the title
/// with its Free / Add-on badge, and the caption. Click, Space or Return selects it; VoiceOver reads
/// it as a radio button with the caption as help.
/// </summary>
public sealed class WinoAccountBenefitTileView : WinoSurfaceView
{
    private bool _isSelected;
    private bool _isHovered;
    private NSTrackingArea? _tracking;

    public WinoAccountBenefitTileView(WinoAccountBenefitItemViewModel benefit)
    {
        Benefit = benefit;
        TranslatesAutoresizingMaskIntoConstraints = false;
        CornerRadius = WinoSettingsStyle.CardRadius;

        var glyph = new WinoIconView(benefit.Icon, 20);
        var box = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, Stroke = WinoSettingsStyle.CardStroke, CornerRadius = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        box.AddSubview(glyph);
        WinoLayout.Size(box, 36, 36);
        NSLayoutConstraint.ActivateConstraints([glyph.CenterXAnchor.ConstraintEqualTo(box.CenterXAnchor), glyph.CenterYAnchor.ConstraintEqualTo(box.CenterYAnchor)]);

        var title = WinoStyle.Label(benefit.Title, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        title.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        // Free and paid badges differ only in tint (Windows: success stroke for free, card stroke for add-ons).
        var badgeText = WinoStyle.Label(benefit.BadgeText, WinoStyle.Caption, benefit.IsFreeBadge ? WinoStyle.Success : WinoStyle.SecondaryText);
        var badge = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.SubtleFill,
            Stroke = benefit.IsFreeBadge ? WinoStyle.Success : WinoSettingsStyle.CardStroke,
            CornerRadius = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(badgeText, badge, 1, 8, 1, 8);
        badge.SetContentHuggingPriorityForOrientation(751, NSLayoutConstraintOrientation.Horizontal);
        var heading = WinoLayout.HStack(8, title, badge);

        var caption = WinoStyle.Label(benefit.Caption, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        caption.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        var text = WinoLayout.VStack(3, heading, caption);
        text.Alignment = NSLayoutAttribute.Leading;
        caption.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
        heading.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;

        var row = WinoLayout.HStack(14, box, text);
        row.Alignment = NSLayoutAttribute.Top;
        WinoLayout.Fill(row, this, 16);

        foreach (var view in new NSView[] { glyph, box, title, badgeText, badge, caption }) view.AccessibilityElement = false;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.RadioButtonRole;
        AccessibilityLabel = $"{benefit.Title}, {benefit.BadgeText}";
        AccessibilityHelp = benefit.Caption;
        AccessibilityIdentifier = benefit.AutomationId;
        Update();
    }

    public WinoAccountBenefitItemViewModel Benefit { get; }

    /// <summary>Raised when the user picks this offer.</summary>
    public event EventHandler? Selected;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Update(); }
    }

    private void Update()
    {
        Fill = _isSelected ? WinoSettingsStyle.SelectedFill : _isHovered ? WinoSettingsStyle.SubtleFill : WinoSettingsStyle.CardFill;
        Stroke = _isSelected ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
        StrokeWidth = _isSelected ? 2 : 1;
        AccessibilityValue = NSNumber.FromBoolean(_isSelected);
    }

    private void Pick()
    {
        Window?.MakeFirstResponder(this);
        Selected?.Invoke(this, EventArgs.Empty);
    }

    public override bool AcceptsFirstResponder() => true;
    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;
    public override void MouseDown(NSEvent theEvent) { }
    public override void MouseUp(NSEvent theEvent)
    {
        if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Pick();
    }

    public override void KeyDown(NSEvent theEvent)
    {
        // Space or Return selects, like a native radio button.
        if (theEvent.KeyCode is 49 or 36 or 76) { Pick(); return; }
        base.KeyDown(theEvent);
    }

    public override bool AccessibilityPerformPress() { Pick(); return true; }

    public override void DrawFocusRingMask() => NSBezierPath.FromRoundedRect(Bounds, (nfloat)CornerRadius, (nfloat)CornerRadius).Fill();
    public override CGRect FocusRingMaskBounds => Bounds;

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null) RemoveTrackingArea(_tracking);
        _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent) { _isHovered = true; Update(); }
    public override void MouseExited(NSEvent theEvent) { _isHovered = false; Update(); }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Update();
    }
}

/// <summary>
/// Detail for the selected offer (Windows WinoAccountBenefitDetailPanel): the offer's illustration
/// in a 300pt column (dropped below a 620pt panel so the copy keeps a readable measure), then the
/// title, lede, what it includes and the call to action.
/// </summary>
public sealed class WinoAccountBenefitDetailPanel : WinoSurfaceView
{
    /// <summary>Below this width the illustration would squeeze the copy, so it gives up its column.</summary>
    private const double ArtMinimumPanelWidth = 620;

    private readonly WinoSurfaceView _art;
    private readonly WinoAccountBenefitIllustrationView _illustration = new();
    private readonly NSTextField _title;
    private readonly NSTextField _lede;
    private readonly NSStackView _points = WinoLayout.VStack(9);

    public WinoAccountBenefitDetailPanel()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Fill = WinoSettingsStyle.CardFill;
        Stroke = WinoSettingsStyle.CardStroke;
        CornerRadius = 8;
        AccessibilityIdentifier = "WinoAccountBenefitDetailPanel";

        _art = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.SubtleFill,
            CornerRadius = 7,
            Corners = CoreAnimation.CACornerMask.MinXMinYCorner | CoreAnimation.CACornerMask.MinXMaxYCorner,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        // Centred vertically: the copy column may be taller than the illustration's aspect allows.
        _art.AddSubview(_illustration);
        NSLayoutConstraint.ActivateConstraints(
        [
            _illustration.LeadingAnchor.ConstraintEqualTo(_art.LeadingAnchor, 24),
            _illustration.TrailingAnchor.ConstraintEqualTo(_art.TrailingAnchor, -24),
            _illustration.CenterYAnchor.ConstraintEqualTo(_art.CenterYAnchor),
            _illustration.TopAnchor.ConstraintGreaterThanOrEqualTo(_art.TopAnchor, 24),
            _illustration.BottomAnchor.ConstraintLessThanOrEqualTo(_art.BottomAnchor, -24)
        ]);
        _art.WidthAnchor.ConstraintEqualTo(300).Active = true;
        var divider = new WinoSeparator(vertical: true) { Fill = WinoSettingsStyle.CardStroke };

        _title = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(20, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        _title.SetValueForKey(new NSString("WinoAccountBenefitDetailTitle"), new NSString("accessibilityIdentifier"));
        _lede = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _points.Alignment = NSLayoutAttribute.Leading;
        CallToAction = SettingsBinder.CreateButton(string.Empty, primary: true);
        CallToAction.KeyEquivalent = string.Empty;
        CallToAction.SetValueForKey(new NSString("WinoAccountBenefitCtaButton"), new NSString("accessibilityIdentifier"));

        var copy = WinoLayout.VStack(14, _title, _lede, _points, CallToAction);
        copy.Alignment = NSLayoutAttribute.Leading;
        copy.EdgeInsets = new NSEdgeInsets(24, 24, 22, 24);
        foreach (var view in new NSView[] { _title, _lede, _points })
        {
            view.WidthAnchor.ConstraintEqualTo(copy.WidthAnchor, 1, -48).Active = true;
            view.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        }
        copy.SetCustomSpacing(18, _points);
        copy.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);

        var row = WinoLayout.HStack(0, _art, divider, copy);
        row.Alignment = NSLayoutAttribute.Height;
        WinoLayout.Fill(row, this);
        UpdateArtVisibility();
    }

    public NSButton CallToAction { get; }

    public void Show(WinoAccountBenefitItemViewModel? benefit)
    {
        _title.StringValue = benefit?.Title ?? string.Empty;
        _lede.StringValue = benefit?.Lede ?? string.Empty;
        CallToAction.Title = benefit?.CtaText ?? string.Empty;
        CallToAction.Hidden = benefit is null;
        _illustration.Kind = benefit?.Type;
        foreach (var view in _points.ArrangedSubviews) { _points.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var point in benefit?.Points ?? [])
        {
            var check = new WinoIconView(WinoIconGlyph.Checkmark, 14, WinoStyle.Accent);
            WinoLayout.Size(check, 16, 16);
            check.AccessibilityElement = false;
            var label = WinoStyle.Label(point, WinoStyle.Body, WinoStyle.SecondaryText, 0);
            label.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
            var line = WinoLayout.HStack(10, check, label);
            line.Alignment = NSLayoutAttribute.Top;
            _points.AddArrangedSubview(line);
            line.WidthAnchor.ConstraintEqualTo(_points.WidthAnchor).Active = true;
        }
    }

    public override void SetFrameSize(CGSize newSize)
    {
        base.SetFrameSize(newSize);
        UpdateArtVisibility();
    }

    private void UpdateArtVisibility()
    {
        var hide = Frame.Width < ArtMinimumPanelWidth;
        if (_art.Hidden == hide) return;
        _art.Hidden = hide;
        if (_art.Superview is NSStackView stack)
            foreach (var view in stack.ArrangedSubviews.OfType<WinoSeparator>()) view.Hidden = hide;
    }
}

/// <summary>
/// The four offer illustrations of the Windows page, drawn from the same 240x170 canvas shapes and
/// scaled uniformly: Move to a new Mac, Add-ons follow you, Wino Intelligence and Unlimited Accounts.
/// Colours are semantic (accent, separator, label, control background) so light and dark both work.
/// </summary>
public sealed class WinoAccountBenefitIllustrationView : NSView
{
    private const double CanvasWidth = 240;
    private const double CanvasHeight = 170;
    private WinoAccountBenefitType? _kind;

    public WinoAccountBenefitIllustrationView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AccessibilityElement = false;
        HeightAnchor.ConstraintEqualTo(WidthAnchor, (nfloat)(CanvasHeight / CanvasWidth)).Active = true;
        WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)CanvasWidth * 1.5f).Active = true;
    }

    public WinoAccountBenefitType? Kind
    {
        get => _kind;
        set { _kind = value; NeedsDisplay = true; }
    }

    public override bool IsFlipped => true;

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        if (_kind is null || NSGraphicsContext.CurrentContext?.CGContext is not { } context) return;
        var scale = Math.Min(Bounds.Width / CanvasWidth, Bounds.Height / CanvasHeight);
        context.SaveState();
        context.TranslateCTM((nfloat)((Bounds.Width - CanvasWidth * scale) / 2), (nfloat)((Bounds.Height - CanvasHeight * scale) / 2));
        context.ScaleCTM((nfloat)scale, (nfloat)scale);
        switch (_kind)
        {
            case WinoAccountBenefitType.DeviceTransfer: DrawDeviceTransfer(); break;
            case WinoAccountBenefitType.Entitlements: DrawEntitlements(); break;
            case WinoAccountBenefitType.Intelligence: DrawIntelligence(); break;
            case WinoAccountBenefitType.UnlimitedAccounts: DrawUnlimitedAccounts(); break;
        }
        context.RestoreState();
    }

    private static NSColor Card => NSColor.ControlBackground;
    private static NSColor Divider => NSColor.Separator;
    private static NSColor Accent(double opacity = 1) => opacity >= 1 ? WinoStyle.Accent : WinoStyle.Accent.ColorWithAlphaComponent((nfloat)opacity);
    /// <summary>Windows TextFillColorTertiary at an extra opacity.</summary>
    private static NSColor Tertiary(double opacity) => NSColor.Label.ColorWithAlphaComponent((nfloat)(0.45 * opacity));

    private static void Rect(double x, double y, double w, double h, double r, NSColor fill, NSColor? stroke = null, double strokeWidth = 1.5)
    {
        var path = NSBezierPath.FromRoundedRect(new CGRect(x, y, w, h), (nfloat)r, (nfloat)r);
        fill.SetFill();
        path.Fill();
        if (stroke is null) return;
        stroke.SetStroke();
        path.LineWidth = (nfloat)strokeWidth;
        path.Stroke();
    }

    private static void Ellipse(double x, double y, double w, double h, NSColor fill)
    {
        fill.SetFill();
        NSBezierPath.FromOvalInRect(new CGRect(x, y, w, h)).Fill();
    }

    private static void StrokePath(string data, NSColor stroke, double width, bool round = true, nfloat[]? dash = null)
    {
        var path = Path(data);
        path.LineWidth = (nfloat)width;
        if (round) path.LineJoinStyle = NSLineJoinStyle.Round;
        if (dash is not null) path.SetLineDash(dash, 0);
        stroke.SetStroke();
        path.Stroke();
    }

    private static void FillPath(string data, NSColor fill)
    {
        fill.SetFill();
        Path(data).Fill();
    }

    private static void DrawDeviceTransfer()
    {
        // Two devices: the old one in neutral strokes, the new one in the accent.
        Rect(6, 34, 88, 98, 9, Card, Divider);
        Rect(16, 46, 52, 7, 3.5, Tertiary(0.5));
        Rect(16, 61, 68, 6, 3, Tertiary(0.28));
        Rect(16, 75, 68, 6, 3, Tertiary(0.28));
        Rect(16, 89, 46, 6, 3, Tertiary(0.28));
        Rect(16, 107, 34, 14, 7, Accent(0.8));

        Rect(146, 34, 88, 98, 9, Card, Accent());
        Rect(156, 46, 52, 7, 3.5, Accent(0.55));
        Rect(156, 61, 68, 6, 3, Tertiary(0.28));
        Rect(156, 75, 68, 6, 3, Tertiary(0.28));
        Rect(156, 89, 46, 6, 3, Tertiary(0.28));
        Rect(156, 107, 34, 14, 7, Accent(0.8));

        // StrokeDashArray "1,3" is in stroke widths (2pt).
        StrokePath("M96,62 C112,40 128,40 144,62", Accent(), 2, round: false, dash: [2, 6]);
        StrokePath("M138,56 L145,62 L138,68", Accent(), 2);
        Ellipse(101, 81, 38, 38, Accent());
        StrokePath("M112,100 L118,106 L128,94", NSColor.White, 2.4);
    }

    private static void DrawEntitlements()
    {
        StrokePath("M43,92 V72 H120 V56 M120,72 H197 V92", Divider, 1.5, round: false);
        Rect(14, 92, 58, 48, 7, Card, Divider);
        Rect(91, 92, 58, 48, 7, Card, Divider);
        Rect(168, 92, 58, 48, 7, Card, Divider);

        const string shield = "M120,22 L152,36 V56 C152,73 139.2,86.4 120,94 C100.8,86.4 88,73 88,56 V36 Z";
        FillPath(shield, Accent(0.16));
        StrokePath(shield, Accent(), 1.8);
        StrokePath("M108,55.5 L116,63.5 L133,46.5", Accent(), 2.6);
        StrokePath("M35,112 L40,117 L50,107 M112,112 L117,117 L127,107 M189,112 L194,117 L204,107", Accent(), 2);
    }

    private static void DrawIntelligence()
    {
        Rect(34, 22, 172, 126, 10, Card, Divider);
        Rect(50, 42, 66, 8, 4, Tertiary(0.45));
        Rect(50, 62, 140, 7, 3.5, Tertiary(0.25));
        Rect(50, 78, 140, 7, 3.5, Tertiary(0.25));
        Rect(50, 94, 96, 7, 3.5, Tertiary(0.25));
        Rect(44, 112, 152, 26, 7, Accent(0.15));
        Rect(56, 121, 104, 8, 4, Accent(0.7));
        FillPath("M183.3,16 L188.5,32 L204.5,37.2 L188.5,42.4 L183.3,58.4 L178.1,42.4 L162.1,37.2 L178.1,32 Z", Accent());
        FillPath("M206.9,44 L209.6,52 L217.6,54.7 L209.6,57.4 L206.9,65.4 L204.2,57.4 L196.2,54.7 L204.2,52 Z", Accent(0.65));
    }

    private static void DrawUnlimitedAccounts()
    {
        Rect(26, 30, 128, 34, 8, Card, Divider);
        Ellipse(36, 37, 20, 20, Accent(0.55));
        Rect(64, 41, 62, 7, 3.5, Tertiary(0.4));
        Rect(64, 52, 40, 5, 2.5, Tertiary(0.22));

        Rect(40, 72, 128, 34, 8, Card, Divider);
        Ellipse(50, 79, 20, 20, Accent(0.4));
        Rect(78, 83, 62, 7, 3.5, Tertiary(0.4));
        Rect(78, 94, 40, 5, 2.5, Tertiary(0.22));

        Rect(54, 114, 128, 34, 8, Card, Accent());
        Ellipse(64, 121, 20, 20, Accent(0.28));
        Rect(92, 125, 62, 7, 3.5, Tertiary(0.4));
        Rect(92, 136, 40, 5, 2.5, Tertiary(0.22));

        // The right lobe runs the other way round, so the strokes cross and read as an infinity mark.
        StrokePath("M196,47 C186,31 176,27 168,31 C158,36 158,58 168,63 C176,67 186,63 196,47 C206,63 216,67 224,63 C234,58 234,36 224,31 C216,27 206,31 196,47 Z", Accent(), 3);
    }

    /// <summary>Parses the absolute SVG/XAML path subset these drawings use: M, L, H, V, C and Z.</summary>
    private static NSBezierPath Path(string data)
    {
        var path = new NSBezierPath();
        var tokens = Tokenize(data);
        var index = 0;
        var command = 'M';
        CGPoint current = default;

        double Next() => double.Parse(tokens[index++], CultureInfo.InvariantCulture);
        bool HasNumber() => index < tokens.Count && !char.IsLetter(tokens[index][0]);

        while (index < tokens.Count)
        {
            if (char.IsLetter(tokens[index][0])) command = tokens[index++][0];
            switch (command)
            {
                case 'M':
                    current = new CGPoint(Next(), Next());
                    path.MoveTo(current);
                    command = 'L';
                    break;
                case 'L':
                    current = new CGPoint(Next(), Next());
                    path.LineTo(current);
                    break;
                case 'H':
                    current = new CGPoint(Next(), current.Y);
                    path.LineTo(current);
                    break;
                case 'V':
                    current = new CGPoint(current.X, Next());
                    path.LineTo(current);
                    break;
                case 'C':
                    var control1 = new CGPoint(Next(), Next());
                    var control2 = new CGPoint(Next(), Next());
                    current = new CGPoint(Next(), Next());
                    path.CurveTo(current, control1, control2);
                    break;
                case 'Z':
                    path.ClosePath();
                    break;
                default:
                    throw new FormatException($"Unsupported path command '{command}'.");
            }
            if (command == 'Z' && HasNumber()) command = 'L';
        }
        return path;
    }

    private static List<string> Tokenize(string data)
    {
        var tokens = new List<string>();
        var number = new System.Text.StringBuilder();
        void Flush() { if (number.Length > 0) { tokens.Add(number.ToString()); number.Clear(); } }
        foreach (var character in data)
        {
            if (char.IsLetter(character)) { Flush(); tokens.Add(character.ToString()); }
            else if (character is ',' or ' ') Flush();
            else if (character == '-' && number.Length > 0) { Flush(); number.Append(character); }
            else number.Append(character);
        }
        Flush();
        return tokens;
    }
}
