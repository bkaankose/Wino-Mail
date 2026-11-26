using System;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.ActionCards;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail.ActionCards;

/// <summary>
/// One card of the reader's action cards (Windows MailRenderingPage action card): a header with the card's glyph, title and
/// summary that opens and closes the body, and a body with the card's headline, trip legs, order lines, delivery steps,
/// copyable fields and actions. A card is read-only; its buttons only report what the user asked for.
/// </summary>
internal sealed class ActionCardView : NSView
{
    private readonly ActionCardModel _model;
    private readonly Action<bool> _expansionChanged;
    private readonly Action<ActionCardAction> _actionRequested;
    private readonly Action<string> _copyRequested;
    private readonly NSStackView _body;
    private NSButton _chevron = null!;
    private bool _expanded;

    public ActionCardView(ActionCardModel model, Action<bool> expansionChanged, Action<ActionCardAction> actionRequested, Action<string> copyRequested)
    {
        _model = model;
        _expansionChanged = expansionChanged;
        _actionRequested = actionRequested;
        _copyRequested = copyRequested;
        _expanded = model.IsExpanded;
        TranslatesAutoresizingMaskIntoConstraints = false;

        var header = BuildHeader();
        _body = WinoLayout.VStack(8, BuildBody());
        _body.Alignment = NSLayoutAttribute.Leading;
        _body.Hidden = !_expanded;

        var stack = WinoLayout.VStack(8, header, _body);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(10, 12, 12, 12);
        var surface = new WinoSurfaceView
        {
            CornerRadius = 8,
            Fill = WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0x1F1F22)),
            Stroke = WinoStyle.ZoneStroke,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(stack, surface);
        WinoLayout.Fill(surface, this);

        UpdateChevron();
        WinoAccessibility.Label(this, _model.Title);
    }

    private NSStackView BuildHeader()
    {
        var icon = new NSImageView { Image = WinoIcons.Image(_model.Glyph, 16), ImageScaling = NSImageScale.ProportionallyDown };
        var title = WinoStyle.Label(_model.Title, WinoStyle.Body, WinoStyle.PrimaryText, 1);
        title.Font = NSFont.BoldSystemFontOfSize(13);
        var summary = WinoStyle.Label(_model.Summary, WinoStyle.Caption, WinoStyle.SecondaryText, 1);
        summary.Hidden = _model.Summary.Length == 0;
        _chevron = new NSButton { BezelStyle = NSBezelStyle.Inline, Bordered = false, Image = ChevronImage(_expanded) };
        _chevron.Activated += (_, _) => Toggle();
        WinoAccessibility.Label(_chevron, _model.Title);

        var header = WinoLayout.HStack(8, icon, title, summary, WinoLayout.Spacer(), _chevron);
        header.Alignment = NSLayoutAttribute.CenterY;
        header.AddGestureRecognizer(new NSClickGestureRecognizer(Toggle));
        return header;
    }

    private NSView[] BuildBody()
    {
        var views = new System.Collections.Generic.List<NSView>();

        if (_model.HasHeadline)
            views.Add(WinoStyle.Label(_model.Headline, WinoStyle.Body, WinoStyle.PrimaryText, 0));
        if (_model.HasSubheadline)
            views.Add(WinoStyle.Label(_model.Subheadline, WinoStyle.Caption, WinoStyle.SecondaryText, 0));

        if (_model.HasLeg)
        {
            var departure = WinoLayout.VStack(2, WinoStyle.Label(_model.DepartureTime, WinoStyle.Body, WinoStyle.PrimaryText, 1),
                WinoStyle.Label(_model.DeparturePlace, WinoStyle.Caption, WinoStyle.SecondaryText, 2));
            var arrival = WinoLayout.VStack(2, WinoStyle.Label(_model.ArrivalTime, WinoStyle.Body, WinoStyle.PrimaryText, 1),
                WinoStyle.Label(_model.ArrivalPlace, WinoStyle.Caption, WinoStyle.SecondaryText, 2));
            views.Add(WinoLayout.HStack(16, departure, WinoStyle.Label("→", WinoStyle.Body, WinoStyle.SecondaryText, 1), arrival));
        }

        if (_model.HasAmount)
        {
            views.Add(WinoLayout.VStack(2,
                WinoStyle.Label(_model.AmountLabel, WinoStyle.Caption, WinoStyle.SecondaryText, 1),
                WinoStyle.Label(_model.Amount, WinoStyle.Body, WinoStyle.PrimaryText, 1)));
        }

        if (_model.HasLines)
        {
            var lines = WinoLayout.VStack(4);
            foreach (var line in _model.Lines)
            {
                var row = WinoLayout.HStack(12, WinoStyle.Label(line.Text, WinoStyle.Body, WinoStyle.PrimaryText, 1), WinoLayout.Spacer(),
                    WinoStyle.Label(line.Amount, WinoStyle.Body, WinoStyle.PrimaryText, 1));
                lines.AddArrangedSubview(row);
            }
            views.Add(lines);
        }

        if (_model.HasTotal)
        {
            var total = WinoStyle.Label(_model.Total, WinoStyle.Body, WinoStyle.PrimaryText, 1);
            total.Font = NSFont.BoldSystemFontOfSize(13);
            views.Add(total);
        }

        if (_model.HasSteps)
        {
            var steps = WinoLayout.HStack(12);
            foreach (var step in _model.Steps)
            {
                var marker = WinoStyle.Label(step.IsReached ? "●" : "○", WinoStyle.Caption, step.IsCurrent ? WinoStyle.Accent : WinoStyle.SecondaryText, 1);
                steps.AddArrangedSubview(WinoLayout.VStack(2, marker, WinoStyle.Label(step.Label, WinoStyle.Caption, WinoStyle.SecondaryText, 2)));
            }
            views.Add(steps);
        }

        if (_model.HasFields)
        {
            var fields = WinoLayout.VStack(4);
            foreach (var field in _model.Fields)
            {
                var row = WinoLayout.HStack(8, WinoStyle.Label(field.Label, WinoStyle.Caption, WinoStyle.SecondaryText, 1),
                    WinoStyle.Label(field.Value, WinoStyle.Body, WinoStyle.PrimaryText, 1), WinoLayout.Spacer());
                if (field.IsCopyable)
                {
                    var value = field.Value;
                    var copy = new NSButton { BezelStyle = NSBezelStyle.Inline, Bordered = false, Image = WinoIcons.Image(WinoIconGlyph.Copy, 14, null, Translator.Buttons_Copy) };
                    copy.Activated += (_, _) => _copyRequested(value);
                    WinoAccessibility.Label(copy, Translator.Buttons_Copy);
                    row.AddArrangedSubview(copy);
                }
                fields.AddArrangedSubview(row);
            }
            views.Add(fields);
        }

        if (_model.HasPrimaryAction || _model.HasMoreActions)
            views.Add(BuildActions());

        return [.. views];
    }

    private NSStackView BuildActions()
    {
        var actions = WinoLayout.HStack(8);
        if (_model.PrimaryAction is { } primary)
            actions.AddArrangedSubview(ActionButton(primary, primary.Label, true));
        foreach (var action in _model.MoreActions)
            actions.AddArrangedSubview(ActionButton(action, action.Label, false));
        actions.AddArrangedSubview(WinoLayout.Spacer());
        actions.AddArrangedSubview(WinoStyle.Label(Translator.ActionCard_SenderDataNote, WinoStyle.Caption, WinoStyle.TertiaryText, 1));
        return actions;
    }

    private NSButton ActionButton(ActionCardAction action, string title, bool primary)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded };
        if (primary)
            button.BezelColor = WinoStyle.Accent;
        button.Activated += (_, _) => _actionRequested(action);
        return button;
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        _body.Hidden = !_expanded;
        UpdateChevron();
        _expansionChanged(_expanded);
    }

    private void UpdateChevron() => _chevron.Image = ChevronImage(_expanded);

    private static NSImage ChevronImage(bool expanded) => WinoIcons.Image(expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, 12);
}
