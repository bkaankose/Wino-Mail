using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views;

/// <summary>Layout pieces of the provider selection wizard (Mac ProviderSelectionPage): cards, headings, the step indicator and tiles.</summary>
public static class ProviderSelectionPage
{
    /// <summary>A Windows card (CardBackgroundFillColorDefault, 8pt radius) hosting a step's content.</summary>
    public static WinoSurfaceView Card(UIView body, double padding = 12)
    {
        var surface = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, CornerRadius = WinoStyle.GroupRadius };
        WinoLayout.Fill(body, surface, padding);
        return surface;
    }

    /// <summary>A semibold title over a wrapping caption, the heading of every step card.</summary>
    public static UIStackView Heading(string title, string description)
    {
        var heading = WinoLayout.VStack(2, WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0),
            WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0));
        heading.Alignment = UIStackViewAlignment.Fill;
        return heading;
    }

    /// <summary>A hairline with a caption in the middle (Windows "or choose a known provider" divider).</summary>
    public static UIView Divider(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.SecondaryText);
        label.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        var left = new WinoSeparator();
        var right = new WinoSeparator();
        var row = WinoLayout.HStack(WinoStyle.Space3, left, label, right);
        row.Alignment = UIStackViewAlignment.Center;
        left.WidthAnchor.ConstraintEqualTo(right.WidthAnchor).Active = true;
        return row;
    }

    /// <summary>The shared provider brand glyph; other provider types show the IMAP glyph.</summary>
    public static WinoIconGlyph Glyph(MailProviderType type, SpecialImapProvider special)
        => MailProviderGlyphs.GetGlyph(type, special) is var glyph && glyph != WinoIconGlyph.None ? glyph : WinoIconGlyph.IMAP;
}

/// <summary>Wizard progress for the footer: one capsule per step, filled up to the current one, and "Step 2 of 3".</summary>
public sealed class WizardStepIndicator : UIView
{
    private readonly List<WinoSurfaceView> _capsules = new();
    private readonly UILabel _label = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private int _current = 1;

    public WizardStepIndicator(int total)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var capsules = WinoLayout.HStack(4);
        for (int index = 0; index < total; index++)
        {
            var capsule = new WinoSurfaceView { CornerRadius = 2.5 };
            WinoLayout.Size(capsule, 18, 5);
            _capsules.Add(capsule);
            capsules.AddArrangedSubview(capsule);
        }
        var stack = WinoLayout.VStack(4, capsules, _label);
        stack.Alignment = UIStackViewAlignment.Leading;
        WinoLayout.Fill(stack, this);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.StaticText;
        WinoStyle.AccentChanged += AccentChanged;
        Apply();
    }

    public void Set(int current, string text)
    {
        _current = current;
        _label.Text = text ?? string.Empty;
        AccessibilityLabel = text;
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

/// <summary>The chosen provider on the steps after the provider step (Windows AccountSummaryPanel): colour strip, glyph, name and detail.</summary>
public sealed class AccountSummaryPanel : WinoSurfaceView
{
    private readonly WinoSurfaceView _strip = new() { CornerRadius = 2 };
    private readonly WinoIconView _icon = new(WinoIconGlyph.IMAP, 28);
    private readonly UILabel _name = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
    private readonly UILabel _detail = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 2);

    public AccountSummaryPanel()
    {
        Fill = WinoStyle.SubtleFill;
        Stroke = WinoSettingsStyle.CardStroke;
        CornerRadius = WinoStyle.GroupRadius;
        WinoLayout.Size(_strip, 4);
        WinoLayout.Size(_icon, 28, 28);
        var text = WinoLayout.VStack(2, _name, _detail);
        text.Alignment = UIStackViewAlignment.Fill;
        var row = WinoLayout.HStack(WinoStyle.Space3, _strip, _icon, text);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(row, this, 12);
        _strip.HeightAnchor.ConstraintEqualTo(text.HeightAnchor, 1, 8).Active = true;
        IsAccessibilityElement = true;
    }

    public void SetProvider(IProviderDetail? provider)
        => _icon.Icon = provider is null ? WinoIconGlyph.IMAP : ProviderSelectionPage.Glyph(provider.Type, provider.SpecialImapProvider);

    public string Name { set { _name.Text = value ?? string.Empty; AccessibilityLabel = value; } }

    public string Detail { set { _detail.Text = value ?? string.Empty; AccessibilityHint = value; } }

    /// <summary>The account colour strip; transparent without a colour.</summary>
    public string? ColorHex { set => _strip.Fill = WinoStyle.FromHexString(value); }
}

/// <summary>A tappable provider tile: the provider's Wino brand glyph, its name and description (Mac ProviderCard).</summary>
public sealed class ProviderCard : WinoPressableView
{
    private bool _isSelected;

    public ProviderCard(IProviderDetail provider)
        : this(ProviderSelectionPage.Glyph(provider.Type, provider.SpecialImapProvider), provider.Name, provider.Description, showsArrow: false)
    {
        Provider = provider;
    }

    public ProviderCard(WinoIconGlyph glyph, string title, string description, bool showsArrow)
    {
        Fill = WinoStyle.GroupFill;
        Stroke = WinoStyle.GroupStroke;
        CornerRadius = 10;
        var icon = new WinoIconView(glyph, 30);
        WinoLayout.Size(icon, 30, 30);
        var text = WinoLayout.VStack(2, WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0),
            WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 2));
        text.Alignment = UIStackViewAlignment.Fill;
        var row = WinoLayout.HStack(WinoStyle.Space3, icon, text);
        if (showsArrow)
        {
            var arrow = new WinoIconView(WinoIconGlyph.ChevronRight, 14, WinoStyle.SecondaryText) { Colorful = false };
            WinoLayout.Size(arrow, 14, 14);
            row.AddArrangedSubview(arrow);
        }
        row.Alignment = UIStackViewAlignment.Center;
        row.UserInteractionEnabled = false;
        WinoLayout.Fill(row, this, 12);
        HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        AccessibilityLabel = title;
        AccessibilityHint = description;
    }

    public IProviderDetail? Provider { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            Stroke = value ? WinoStyle.Accent : WinoStyle.GroupStroke;
            StrokeWidth = value ? 2 : 1;
            AccessibilityTraits = value ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        }
    }
}
