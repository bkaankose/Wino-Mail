using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Views.Onboarding;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

/// <summary>
/// Provider selection layout (Windows ProviderSelectionPage): the step title and subtitle, the
/// current step's content in a centred column, and a pinned footer with the step indicator on the
/// leading side and Back and the primary button on the trailing side.
/// </summary>
public sealed class ProviderSelectionPage : NSView
{
    /// <summary>The Windows page caps its grid at 880; the Mac column reads better a little narrower.</summary>
    public const double MaxContentWidth = 720;

    public ProviderSelectionPage(NSTextField title, NSTextField subtitle, NSView stepIndicator, NSView content, NSButton back, NSButton primary)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        title.Font = WinoStyle.PageTitle;
        title.TextColor = WinoStyle.PrimaryText;
        subtitle.Font = WinoStyle.Body;
        subtitle.TextColor = WinoStyle.SecondaryText;
        subtitle.MaximumNumberOfLines = 0;
        subtitle.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        subtitle.PreferredMaxLayoutWidth = 600;
        subtitle.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var header = WinoLayout.VStack(6, title, subtitle);
        var column = WinoLayout.VStack(20, header, content);
        header.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        content.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;

        var document = new FlippedView();
        document.AddSubview(column);
        var preferredWidth = column.WidthAnchor.ConstraintEqualTo(document.WidthAnchor, 1, -96);
        preferredWidth.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(document.TopAnchor, 36),
            column.BottomAnchor.ConstraintEqualTo(document.BottomAnchor, -24),
            column.CenterXAnchor.ConstraintEqualTo(document.CenterXAnchor),
            column.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)MaxContentWidth),
            column.WidthAnchor.ConstraintLessThanOrEqualTo(document.WidthAnchor, 1, -48),
            preferredWidth
        ]);

        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
            DocumentView = document
        };
        document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;
        document.TopAnchor.ConstraintEqualTo(scroll.ContentView.TopAnchor).Active = true;
        document.LeadingAnchor.ConstraintEqualTo(scroll.ContentView.LeadingAnchor).Active = true;

        primary.KeyEquivalent = "\r";
        primary.WidthAnchor.ConstraintGreaterThanOrEqualTo(120).Active = true;
        var footer = WinoLayout.HStack(WinoStyle.Space2, stepIndicator, WinoLayout.Spacer(), back, primary);
        footer.EdgeInsets = new NSEdgeInsets(14, 24, 18, 24);
        var separator = new WinoSeparator();

        AddSubview(scroll);
        AddSubview(separator);
        AddSubview(footer);
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(separator.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            separator.BottomAnchor.ConstraintEqualTo(footer.TopAnchor),
            footer.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            footer.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            footer.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
    }

    /// <summary>A Windows card (CardBackgroundFillColorDefault, 8pt radius) hosting a step's content.</summary>
    public static WinoSurfaceView Card(NSView body, double padding = 12)
    {
        var surface = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, CornerRadius = WinoStyle.GroupRadius };
        WinoLayout.Fill(body, surface, padding);
        return surface;
    }

    /// <summary>A semibold title over a wrapping caption, the heading of every Windows step card.</summary>
    public static NSStackView Heading(string title, string description)
    {
        var caption = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var heading = WinoLayout.VStack(2, WinoStyle.Label(title, WinoStyle.BodyStrong), caption);
        caption.WidthAnchor.ConstraintEqualTo(heading.WidthAnchor).Active = true;
        return heading;
    }

    /// <summary>A hairline with a caption in the middle (Windows "or choose a known provider" divider).</summary>
    public static NSView Divider(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.SecondaryText);
        label.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        var left = new WinoSeparator();
        var right = new WinoSeparator();
        var row = WinoLayout.HStack(WinoStyle.Space3, left, label, right);
        left.WidthAnchor.ConstraintEqualTo(right.WidthAnchor).Active = true;
        return row;
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }
}

/// <summary>
/// Wizard progress for the footer: one capsule per step, filled up to the current one, and the
/// "Step 2 of 3" text for VoiceOver and for readers who prefer words to shapes.
/// </summary>
public sealed class WizardStepIndicator : NSView
{
    private readonly List<WinoSurfaceView> _capsules = new();
    private readonly NSTextField _label = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private int _current = 1;

    public WizardStepIndicator(int total)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var capsules = WinoLayout.HStack(4);
        for (int index = 0; index < total; index++)
        {
            var capsule = new WinoSurfaceView { CornerRadius = 2.5 };
            WinoLayout.Size(capsule, 22, 5);
            _capsules.Add(capsule);
            capsules.AddArrangedSubview(capsule);
        }
        var row = WinoLayout.HStack(WinoStyle.Space3, capsules, _label);
        WinoLayout.Fill(row, this);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        WinoStyle.AccentChanged += AccentChanged;
        Apply();
    }

    public void Set(int current, string text)
    {
        _current = current;
        _label.StringValue = text ?? string.Empty;
        AccessibilityLabel = text;
        Apply();
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Apply();
    }

    private void AccentChanged(object? sender, EventArgs args) => Apply();

    private void Apply()
    {
        for (int index = 0; index < _capsules.Count; index++)
            _capsules[index].Fill = index < _current ? WinoStyle.Accent : WinoStyle.SubtleFill;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// The chosen provider, kept in view on the steps after the provider step (Windows
/// AccountSummaryPanel): the account colour strip, the provider glyph, its name and the account
/// name, and the account colour button.
/// </summary>
public sealed class AccountSummaryPanel : WinoSurfaceView
{
    private readonly WinoSurfaceView _strip = new() { CornerRadius = 2 };
    private readonly WinoIconView _icon = new(WinoIconGlyph.IMAP, 28);
    private readonly NSTextField _name = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
    private readonly NSTextField _detail = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 2);

    internal AccountSummaryPanel(NSView colorButton)
    {
        Fill = WinoStyle.SubtleFill;
        Stroke = WinoSettingsStyle.CardStroke;
        CornerRadius = WinoStyle.GroupRadius;
        WinoLayout.Size(_strip, 4);
        WinoLayout.Size(_icon, 28, 28);
        var text = WinoLayout.VStack(2, _name, _detail);
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        _detail.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var row = WinoLayout.HStack(WinoStyle.Space3, _strip, _icon, text, WinoLayout.Spacer(), colorButton);
        row.EdgeInsets = new NSEdgeInsets(12, 12, 12, 12);
        WinoLayout.Fill(row, this);
        _strip.HeightAnchor.ConstraintEqualTo(text.HeightAnchor, 1, 8).Active = true;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.GroupRole;
    }

    public void SetProvider(IProviderDetail? provider)
    {
        _icon.Icon = provider is null ? WinoIconGlyph.IMAP : ProviderCard.Glyph(provider.Type, provider.SpecialImapProvider);
    }

    public string Name
    {
        set { _name.StringValue = value ?? string.Empty; AccessibilityLabel = value; }
    }

    public string Detail
    {
        set { _detail.StringValue = value ?? string.Empty; AccessibilityHelp = value; }
    }

    /// <summary>The account colour strip; transparent without a colour, like the Windows null brush.</summary>
    public string? ColorHex
    {
        set => _strip.Fill = WinoStyle.FromHexString(value);
    }
}

/// <summary>
/// A clickable provider tile: the provider's Wino brand glyph (the same glyph Windows shows), its
/// name and description. The same tile without a provider is a navigation row (More providers).
/// </summary>
public sealed class ProviderCard : WinoSurfaceView
{
    private bool _isSelected;

    public ProviderCard(IProviderDetail provider)
        : this(Glyph(provider.Type, provider.SpecialImapProvider), provider.Name, provider.Description, showsArrow: false)
    {
        Provider = provider;
    }

    public ProviderCard(WinoIconGlyph glyph, string title, string description, bool showsArrow)
    {
        Fill = WinoStyle.GroupFill;
        Stroke = WinoStyle.GroupStroke;
        CornerRadius = 10;

        var icon = new WinoIconView(glyph, 32);
        WinoLayout.Size(icon, 32, 32);

        var name = WinoStyle.Label(title, NSFont.SystemFontOfSize(13, NSFontWeight.Semibold));
        var detail = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 2);
        detail.PreferredMaxLayoutWidth = 220;
        var text = WinoLayout.VStack(2, name, detail);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var row = WinoLayout.HStack(WinoStyle.Space3, icon, text);
        if (showsArrow)
        {
            var arrow = new WinoIconView(WinoIconGlyph.ArrowRight, 14, WinoStyle.SecondaryText) { Colorful = false };
            WinoLayout.Size(arrow, 14, 14);
            row.AddArrangedSubview(WinoLayout.Spacer());
            row.AddArrangedSubview(arrow);
        }
        row.Alignment = NSLayoutAttribute.CenterY;
        row.EdgeInsets = new NSEdgeInsets(12, 14, 12, 14);
        WinoLayout.Fill(row, this);

        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ButtonRole;
        AccessibilityLabel = title;
        AccessibilityHelp = description;
    }

    /// <summary>Same mapping as the Windows XamlHelpers.GetProviderIcon.</summary>
    public static WinoIconGlyph Glyph(MailProviderType type, SpecialImapProvider special)
    {
        if (special == SpecialImapProvider.None)
        {
            return type switch
            {
                MailProviderType.Outlook => WinoIconGlyph.Microsoft,
                MailProviderType.Gmail => WinoIconGlyph.Google,
                _ => WinoIconGlyph.IMAP
            };
        }
        return special switch
        {
            SpecialImapProvider.iCloud => WinoIconGlyph.Apple,
            SpecialImapProvider.Yahoo => WinoIconGlyph.Yahoo,
            _ => Enum.TryParse<WinoIconGlyph>(special.ToString(), out var brand) ? brand : WinoIconGlyph.IMAP
        };
    }

    public IProviderDetail? Provider { get; }
    public event EventHandler? Activated;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            Stroke = value ? WinoStyle.Accent : WinoStyle.GroupStroke;
            StrokeWidth = value ? 2 : 1;
        }
    }

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;
    public override bool AcceptsFirstResponder() => true;

    public override void MouseUp(NSEvent theEvent)
    {
        if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Activated?.Invoke(this, EventArgs.Empty);
    }

    public override void KeyDown(NSEvent theEvent)
    {
        if (theEvent.Characters is "\r" or " ") { Activated?.Invoke(this, EventArgs.Empty); return; }
        base.KeyDown(theEvent);
    }

    public override bool AccessibilityPerformPress()
    {
        Activated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.PointingHandCursor);

    // Keyboard focus draws the system focus ring around the rounded tile.
    public override void DrawFocusRingMask() => NSBezierPath.FromRoundedRect(Bounds, 10, 10).Fill();
    public override CGRect FocusRingMaskBounds => Bounds;
}
