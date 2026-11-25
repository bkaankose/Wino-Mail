using Wino.Core.Domain;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Extras;

/// <summary>Everything one briefing card shows, already localised; the head builds it from a DailyBriefingItem.</summary>
public sealed record WinoBriefingCardModel(
    string Headline,
    string Time,
    bool IsNew,
    string? Summary,
    string? Urgency,
    string[] Labels,
    string ActionGlyph,
    string ActionText,
    string IgnoreGlyph,
    string IgnoreTooltip,
    bool CanToggleIgnore);

/// <summary>
/// One Daily briefing card (Windows DailyBriefingPanel BriefingItemTemplate, macOS WinoBriefingCardView): the headline
/// with the arrival time and the accent "new" dot, the summary, and the urgency and label chips beside the primary
/// action and the ignore button. Model text is plain; it is never markup.
/// </summary>
public sealed class WinoBriefingCard : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
    private readonly Gtk.Label _headline = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _newDot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Label _time = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _summary = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _chips = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
    private readonly Gtk.Button _primary = Gtk.Button.New();
    private readonly Gtk.Label _primaryGlyph;
    private readonly Gtk.Label _primaryText = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _ignore;
    private readonly List<IDisposable> _subscriptions = [];

    public WinoBriefingCard()
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass("wino-briefing-card");
        _headline.AddCssClass("heading");
        _headline.SetXalign(0);
        _headline.SetHexpand(true);
        _headline.SetWrap(true);
        _headline.SetWrapMode(Pango.WrapMode.WordChar);
        _headline.SetLines(2);
        _headline.SetEllipsize(Pango.EllipsizeMode.End);
        _newDot.AddCssClass("wino-briefing-new");
        _newDot.SetSizeRequest(6, 6);
        _newDot.SetValign(Gtk.Align.Center);
        WinoA11y.Label(_newDot, Translator.DailyBriefing_New);
        _time.AddCssClass("caption");
        _time.AddCssClass("dim-label");
        var meta = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        meta.SetValign(Gtk.Align.Start);
        meta.SetMarginTop(2);
        meta.Append(_newDot);
        meta.Append(_time);
        var headlineRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        headlineRow.Append(_headline);
        headlineRow.Append(meta);

        _summary.AddCssClass("caption");
        _summary.AddCssClass("dim-label");
        _summary.SetXalign(0);
        _summary.SetWrap(true);
        _summary.SetWrapMode(Pango.WrapMode.WordChar);
        _summary.SetLines(2);
        _summary.SetEllipsize(Pango.EllipsizeMode.End);

        _chips.SetHexpand(true);
        _chips.SetValign(Gtk.Align.Center);
        _primaryGlyph = WinoIcons.Label(string.Empty, 12);
        var primaryContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        primaryContent.Append(_primaryGlyph);
        primaryContent.Append(_primaryText);
        _primary.SetChild(primaryContent);
        _primary.AddCssClass("wino-briefing-action");
        _primary.SetValign(Gtk.Align.Center);
        _ignore = Gtk.Button.New();
        _ignore.AddCssClass("flat");
        _ignore.SetChild(WinoIcons.Label(string.Empty, 12));
        _ignore.SetValign(Gtk.Align.Center);
        _subscriptions.Add(GtkSignals.OnClicked(_primary, () => PrimaryActivated?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_ignore, () => IgnoreActivated?.Invoke()));
        var actionRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        actionRow.SetMarginTop(4);
        actionRow.Append(_chips);
        actionRow.Append(_primary);
        actionRow.Append(_ignore);

        _root.Append(headlineRow);
        _root.Append(_summary);
        _root.Append(actionRow);
    }

    public Gtk.Widget Root => _root;

    public Gtk.Button PrimaryButton => _primary;

    public Gtk.Button IgnoreButton => _ignore;

    /// <summary>The card's one command; the owner routes it to the ViewModel.</summary>
    public event Action? PrimaryActivated;

    public event Action? IgnoreActivated;

    public void Apply(WinoBriefingCardModel model)
    {
        _headline.SetText(model.Headline);
        _time.SetText(model.Time);
        _newDot.SetVisible(model.IsNew);
        _summary.SetText(model.Summary ?? string.Empty);
        _summary.SetVisible(!string.IsNullOrWhiteSpace(model.Summary));
        while (_chips.GetFirstChild() is { } chip) _chips.Remove(chip);
        if (model.Urgency is { Length: > 0 } urgency)
        {
            var chip = new WinoChip(20) { Text = urgency, IsWarning = true, MaxWidthChars = 16 };
            _chips.Append(chip.Root);
        }
        foreach (var label in model.Labels) _chips.Append(new WinoChip(20) { Text = label, MaxWidthChars = 16 }.Root);
        WinoIcons.SetGlyph(_primaryGlyph, model.ActionGlyph);
        _primaryText.SetText(model.ActionText);
        WinoA11y.Rename(_primary, model.ActionText);
        if (_ignore.GetChild() is Gtk.Label ignoreGlyph) WinoIcons.SetGlyph(ignoreGlyph, model.IgnoreGlyph);
        _ignore.SetTooltipText(model.IgnoreTooltip);
        WinoA11y.Rename(_ignore, model.IgnoreTooltip);
        _ignore.SetSensitive(model.CanToggleIgnore);
        WinoA11y.Label(_root, model.Headline);
    }

    public void Dispose()
    {
        PrimaryActivated = null;
        IgnoreActivated = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }
}
