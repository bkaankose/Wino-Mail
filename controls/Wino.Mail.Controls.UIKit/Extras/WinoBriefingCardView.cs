using UIKit;
using Wino.Core.Domain;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Extras;

/// <summary>Everything one briefing card shows, already localised. Built by the panel from a DailyBriefingItem.</summary>
public sealed record WinoBriefingCardData(
    string Headline,
    string Time,
    bool IsNew,
    string? Summary,
    string? Urgency,
    IReadOnlyList<string> Labels,
    string ActionGlyph,
    string ActionText,
    string IgnoreGlyph,
    string IgnoreTooltip,
    bool CanToggleIgnore);

/// <summary>
/// One Daily briefing card (Windows DailyBriefingPanel BriefingItemTemplate; UIKit twin of the AppKit
/// <c>WinoBriefingCardView</c>): headline with the arrival time and the accent "new" dot opposite it, the summary, and
/// the urgency and label chips beside the primary action and the ignore button. Padding 12×10, card stroke.
/// </summary>
public sealed class WinoBriefingCardView : WinoSurfaceView
{
    /// <summary>Windows CardBackgroundFillColorDefault / CardStrokeColorDefault.</summary>
    public static UIColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.7), WinoStyle.Hex(0xFFFFFF, 0.05));
    public static UIColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0x000000, 0.10));

    /// <summary>Windows SystemFillColorCautionBackground / SystemFillColorCaution.</summary>
    public static UIColor CautionFill => WinoStyle.Dynamic(WinoStyle.Hex(0xFFF4CE), WinoStyle.Hex(0x433519));
    public static UIColor CautionText => WinoStyle.Dynamic(WinoStyle.Hex(0x9D5D00), WinoStyle.Hex(0xFCE100));

    private readonly UILabel _headline;
    private readonly WinoSurfaceView _newDot;
    private readonly UILabel _time;
    private readonly UILabel _summary;
    private readonly UIStackView _chips;
    private readonly UIButton _primary;
    private readonly UIButton _ignore;

    public WinoBriefingCardView()
    {
        CornerRadius = WinoStyle.GroupRadius;
        Fill = CardFill;
        Stroke = CardStroke;

        _headline = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 2);
        WinoLayout.Priorities(_headline, UILayoutConstraintAxis.Horizontal, 1, 250);
        _newDot = new WinoSurfaceView { CornerRadius = 3, Fill = WinoStyle.Accent };
        WinoLayout.Size(_newDot, 6, 6);
        WinoAccessibility.Label(_newDot, Translator.DailyBriefing_New);
        _time = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);
        var meta = WinoLayout.HStack(6, _newDot, _time);
        WinoLayout.Priorities(meta, UILayoutConstraintAxis.Horizontal, 751, 751);
        var headlineRow = WinoLayout.HStack(8, _headline, meta);
        headlineRow.Alignment = UIStackViewAlignment.Top;

        _summary = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 3);

        _chips = WinoLayout.HStack(4);
        WinoLayout.Priorities(_chips, UILayoutConstraintAxis.Horizontal, 250, 250);

        var primaryConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        primaryConfiguration.ButtonSize = UIButtonConfigurationSize.Small;
        primaryConfiguration.ImagePadding = 4;
        _primary = new UIButton { Configuration = primaryConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        _primary.PrimaryActionTriggered += (_, _) => PrimaryActivated?.Invoke(this, EventArgs.Empty);
        var ignoreConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        _ignore = new UIButton { Configuration = ignoreConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(_ignore, 36, 32);
        _ignore.PrimaryActionTriggered += (_, _) => IgnoreActivated?.Invoke(this, EventArgs.Empty);
        var actions = WinoLayout.HStack(4, _primary, _ignore);
        WinoLayout.Priorities(actions, UILayoutConstraintAxis.Horizontal, 751, 751);
        var actionRow = WinoLayout.HStack(8, _chips, WinoLayout.Spacer(), actions);

        var stack = WinoLayout.Insets(WinoLayout.VStack(4, headlineRow, _summary, actionRow), 10, 12, 10, 12);
        stack.Alignment = UIStackViewAlignment.Fill;
        stack.SetCustomSpacing(8, _summary);
        WinoLayout.Fill(stack, this);
        ShouldGroupAccessibilityChildren = true;
    }

    /// <summary>The card's one command; the owner routes it to the ViewModel.</summary>
    public event EventHandler? PrimaryActivated;

    public event EventHandler? IgnoreActivated;

    public void Update(WinoBriefingCardData data)
    {
        _headline.Text = data.Headline;
        _time.Text = data.Time;
        _newDot.Hidden = !data.IsNew;
        _newDot.Fill = WinoStyle.Accent;
        _summary.Text = data.Summary ?? string.Empty;
        _summary.Hidden = string.IsNullOrWhiteSpace(data.Summary);

        foreach (var view in _chips.ArrangedSubviews) view.RemoveFromSuperview();
        if (data.Urgency is { Length: > 0 } urgency)
        {
            var chip = Chip(urgency);
            chip.Fill = CautionFill;
            chip.TextColor = CautionText;
            _chips.AddArrangedSubview(chip);
        }
        foreach (var label in data.Labels) _chips.AddArrangedSubview(Chip(label));

        if (_primary.Configuration is { } primary)
        {
            primary.Title = data.ActionText;
            primary.Image = WinoIcons.Image(data.ActionGlyph, 12);
            _primary.Configuration = primary;
        }
        if (_ignore.Configuration is { } ignore)
        {
            ignore.Image = WinoIcons.Image(data.IgnoreGlyph, 14, null, data.IgnoreTooltip);
            _ignore.Configuration = ignore;
        }
        _ignore.Enabled = data.CanToggleIgnore;
        WinoAccessibility.Label(_ignore, data.IgnoreTooltip);
        AccessibilityLabel = data.Headline;
    }

    private static WinoChipView Chip(string text)
    {
        var chip = new WinoChipView(22) { Text = text };
        chip.WidthAnchor.ConstraintLessThanOrEqualTo(124).Active = true;
        return chip;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PrimaryActivated = null;
            IgnoreActivated = null;
        }
        base.Dispose(disposing);
    }
}
