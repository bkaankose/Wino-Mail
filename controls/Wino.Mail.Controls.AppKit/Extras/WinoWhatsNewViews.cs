using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Extras;

/// <summary>
/// One version in the What's New selector (Windows SelectorBarItem): a 30pt pill, accent-filled
/// with white semibold text when selected, card-filled otherwise, with a star for highlighted releases.
/// </summary>
public sealed class WinoVersionTab : WinoPressableView
{
    private readonly NSTextField _label;
    private readonly WinoIconView _star;
    private bool _selected;

    public WinoVersionTab(string version, bool isStarred)
    {
        CornerRadius = 6;
        HeightAnchor.ConstraintEqualTo(30).Active = true;
        _star = new WinoIconView(WinoIconGlyph.StarFilled, 12) { Hidden = !isStarred };
        _label = WinoStyle.Label(version, WinoStyle.Body, WinoStyle.PrimaryText);
        var row = WinoLayout.HStack(6, _star, _label);
        row.EdgeInsets = new NSEdgeInsets(0, 12, 0, 12);
        WinoLayout.Fill(row, this);
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        AccessibilityLabel = version;
        // One of a growing list of releases, so a pill row rather than a fixed NSSegmentedControl;
        // VoiceOver reads the tabs as a radio group.
        AccessibilityRole = NSAccessibilityRoles.RadioButtonRole;
        Apply();
    }

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Apply(); }
    }

    private void Apply()
    {
        Fill = _selected ? WinoStyle.Accent : WinoBriefingCardView.CardFill;
        Stroke = _selected ? null : WinoBriefingCardView.CardStroke;
        _label.Font = _selected ? WinoStyle.BodyStrong : WinoStyle.Body;
        _label.TextColor = _selected ? NSColor.White : WinoStyle.PrimaryText;
        _star.Tint = _selected ? NSColor.White : WinoStyle.PrimaryText;
        AccessibilitySelected = _selected;
    }
}

/// <summary>
/// One feature in the What's New list (Windows ListViewItem, padding 16×12, radius 6): a
/// BodyStrong title and a three-line caption description, highlighted when selected.
/// </summary>
public sealed class WinoFeatureRow : WinoPressableView
{
    private bool _selected;

    public WinoFeatureRow(string title, string description)
    {
        CornerRadius = 6;
        var titleLabel = WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        var descriptionLabel = WinoStyle.Label(description, WinoStyle.Caption, WinoStyle.SecondaryText, 3);
        var stack = WinoLayout.VStack(4, titleLabel, descriptionLabel);
        stack.EdgeInsets = new NSEdgeInsets(12, 16, 12, 16);
        foreach (var label in new[] { titleLabel, descriptionLabel })
            label.TrailingAnchor.ConstraintEqualTo(stack.TrailingAnchor, -16).Active = true;
        WinoLayout.Fill(stack, this);
        AccessibilityLabel = string.IsNullOrWhiteSpace(description) ? title : $"{title}. {description}";
        Apply();
    }

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Apply(); }
    }

    private void Apply()
    {
        Fill = _selected ? WinoStyle.SubtleFill : null;
        AccessibilitySelected = _selected;
    }
}

/// <summary>Draws an image aspect-filled and centred (Windows Image Stretch=UniformToFill), clipped to its bounds.</summary>
public sealed class WinoAspectFillImageView : NSView
{
    private NSImage? _image;

    public WinoAspectFillImageView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Layer!.MasksToBounds = true;
    }

    public NSImage? Image
    {
        get => _image;
        set { _image = value; NeedsDisplay = true; }
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        WinoStyle.SubtleFill.SetFill();
        NSGraphics.RectFill(Bounds);
        if (_image is null || _image.Size.Width <= 0 || _image.Size.Height <= 0) return;
        var scale = Math.Max(Bounds.Width / _image.Size.Width, Bounds.Height / _image.Size.Height);
        var size = new CGSize(_image.Size.Width * scale, _image.Size.Height * scale);
        var target = new CGRect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        _image.Draw(target, CGRect.Empty, NSCompositingOperation.SourceOver, 1);
    }
}
