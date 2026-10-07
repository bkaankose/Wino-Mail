using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Extras;

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
/// One Daily briefing card (Windows DailyBriefingPanel BriefingItemTemplate): headline with the
/// arrival time and the accent "new" dot opposite it, the one-line summary, and the urgency and
/// label chips beside the primary action and the ignore button. Padding 12×10, radius 4, card stroke.
/// </summary>
public sealed class WinoBriefingCardView : WinoSurfaceView
{
    /// <summary>Windows CardBackgroundFillColorDefault / CardStrokeColorDefault.</summary>
    public static NSColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.7), WinoStyle.Hex(0xFFFFFF, 0.05));
    public static NSColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0x000000, 0.10));

    /// <summary>Windows SystemFillColorCautionBackground / SystemFillColorCaution.</summary>
    public static NSColor CautionFill => WinoStyle.Dynamic(WinoStyle.Hex(0xFFF4CE), WinoStyle.Hex(0x433519));
    public static NSColor CautionText => WinoStyle.Dynamic(WinoStyle.Hex(0x9D5D00), WinoStyle.Hex(0xFCE100));

    private readonly NSTextField _headline;
    private readonly WinoSurfaceView _newDot;
    private readonly NSTextField _time;
    private readonly NSTextField _summary;
    private readonly NSStackView _chips;
    private readonly NSButton _primary;
    private readonly NSButton _ignore;

    public WinoBriefingCardView()
    {
        CornerRadius = 4;
        Fill = CardFill;
        Stroke = CardStroke;

        _headline = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 2);
        _newDot = new WinoSurfaceView { CornerRadius = 3, Fill = WinoStyle.Accent };
        WinoLayout.Size(_newDot, 6, 6);
        WinoAccessibility.Label(_newDot, Wino.Core.Domain.Translator.DailyBriefing_New);
        _time = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);
        var meta = WinoLayout.HStack(6, _newDot, _time);
        meta.SetContentHuggingPriorityForOrientation(751, NSLayoutConstraintOrientation.Horizontal);
        var headlineRow = WinoLayout.HStack(8, _headline, meta);
        headlineRow.Alignment = NSLayoutAttribute.Top;
        // The time hugs the first text line so a two-line headline does not drag it down.
        meta.TopAnchor.ConstraintEqualTo(headlineRow.TopAnchor, 1).Active = true;

        _summary = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 2);

        _chips = WinoLayout.HStack(4);
        _chips.SetContentHuggingPriorityForOrientation(250, NSLayoutConstraintOrientation.Horizontal);
        _primary = new NSButton
        {
            BezelStyle = NSBezelStyle.Rounded,
            ControlSize = NSControlSize.Small,
            ImagePosition = NSCellImagePosition.ImageLeading,
            Font = NSFont.SystemFontOfSize(12),
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _primary.Activated += (_, _) => PrimaryActivated?.Invoke(this, EventArgs.Empty);
        _ignore = new NSButton
        {
            Bordered = false,
            ImagePosition = NSCellImagePosition.ImageOnly,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(_ignore, 30, 28);
        _ignore.Activated += (_, _) => IgnoreActivated?.Invoke(this, EventArgs.Empty);
        var actions = WinoLayout.HStack(4, _primary, _ignore);
        actions.SetContentHuggingPriorityForOrientation(751, NSLayoutConstraintOrientation.Horizontal);
        var actionRow = WinoLayout.HStack(8, _chips, actions);

        var stack = WinoLayout.VStack(4, headlineRow, _summary, actionRow);
        stack.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        stack.SetCustomSpacing(8, _summary);
        foreach (var row in new NSView[] { headlineRow, _summary, actionRow })
        {
            row.LeadingAnchor.ConstraintEqualTo(stack.LeadingAnchor, 12).Active = true;
            row.TrailingAnchor.ConstraintEqualTo(stack.TrailingAnchor, -12).Active = true;
        }
        WinoLayout.Fill(stack, this);
    }

    /// <summary>The card's one command; the owner routes it to the ViewModel.</summary>
    public event EventHandler? PrimaryActivated;

    public event EventHandler? IgnoreActivated;

    public void Update(WinoBriefingCardData data)
    {
        _headline.StringValue = data.Headline;
        _time.StringValue = data.Time;
        _newDot.Hidden = !data.IsNew;
        _newDot.Fill = WinoStyle.Accent;
        _summary.StringValue = data.Summary ?? string.Empty;
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

        _primary.Title = data.ActionText;
        _primary.Image = WinoIcons.Image(data.ActionGlyph, 12);
        _ignore.Image = WinoIcons.Image(data.IgnoreGlyph, 12);
        _ignore.ToolTip = data.IgnoreTooltip;
        _ignore.Enabled = data.CanToggleIgnore;
        WinoAccessibility.Label(_ignore, data.IgnoreTooltip);
        AccessibilityLabel = data.Headline;
    }

    private static WinoChipView Chip(string text)
    {
        var chip = new WinoChipView(20) { Text = text };
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
