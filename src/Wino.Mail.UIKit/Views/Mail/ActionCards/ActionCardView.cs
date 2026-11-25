using System;
using System.Collections.Generic;
using System.Linq;
using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels.ActionCards;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail.ActionCards;

/// <summary>
/// One card of the reader's action cards (Windows MailRenderingPage action card, Mac ActionCardView): a header with the
/// glyph, title and summary that opens and closes the body, and a body with the headline, trip legs, order lines,
/// delivery steps, copyable fields and actions. The card is read-only; its controls only report what the user asked for.
/// </summary>
internal sealed class ActionCardView : WinoSurfaceView
{
    private readonly ActionCardModel _model;
    private readonly Action<bool> _expansionChanged;
    private readonly Action<ActionCardAction> _actionRequested;
    private readonly Action<string> _copyRequested;
    private readonly UIStackView _body;
    private readonly UIImageView _chevron;
    private bool _expanded;

    public ActionCardView(ActionCardModel model, Action<bool> expansionChanged, Action<ActionCardAction> actionRequested, Action<string> copyRequested)
    {
        _model = model;
        _expansionChanged = expansionChanged;
        _actionRequested = actionRequested;
        _copyRequested = copyRequested;
        _expanded = model.IsExpanded;
        TranslatesAutoresizingMaskIntoConstraints = false;
        CornerRadius = 10;
        Fill = UIColor.SecondarySystemGroupedBackground;
        Stroke = WinoStyle.ZoneStroke;
        StrokeWidth = 1;

        _chevron = new UIImageView { ContentMode = UIViewContentMode.ScaleAspectFit, TintColor = WinoStyle.SecondaryText };
        _chevron.TranslatesAutoresizingMaskIntoConstraints = false;
        _chevron.WidthAnchor.ConstraintEqualTo(14).Active = true;
        _chevron.HeightAnchor.ConstraintEqualTo(14).Active = true;

        var icon = new UIImageView(WinoIcons.Image(_model.Glyph, 18, WinoStyle.Accent)) { ContentMode = UIViewContentMode.ScaleAspectFit };
        icon.TranslatesAutoresizingMaskIntoConstraints = false;
        icon.WidthAnchor.ConstraintEqualTo(20).Active = true;
        var title = WinoStyle.Label(_model.Title, WinoStyle.Subheadline, WinoStyle.PrimaryText, 1);
        title.Font = UIFont.BoldSystemFontOfSize(15);
        var summary = WinoStyle.Label(_model.Summary, WinoStyle.Caption, WinoStyle.SecondaryText, 1);
        summary.Hidden = _model.Summary.Length == 0;
        summary.SetContentHuggingPriority(250, UILayoutConstraintAxis.Horizontal);
        var header = WinoLayout.HStack(8, icon, title, summary, _chevron);
        header.Alignment = UIStackViewAlignment.Center;
        header.UserInteractionEnabled = true;
        header.AddGestureRecognizer(new UITapGestureRecognizer(Toggle));
        header.IsAccessibilityElement = true;
        header.AccessibilityLabel = _model.Title;
        header.AccessibilityTraits = UIAccessibilityTrait.Button;

        _body = WinoLayout.VStack(8, BuildBody());
        _body.Alignment = UIStackViewAlignment.Fill;
        _body.Hidden = !_expanded;

        var stack = WinoLayout.VStack(8, header, _body);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, this, 12, 14, 14, 14);
        UpdateChevron();
    }

    private UIView[] BuildBody()
    {
        var views = new List<UIView>();

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
                lines.AddArrangedSubview(WinoLayout.HStack(12, WinoStyle.Label(line.Text, WinoStyle.Body, WinoStyle.PrimaryText, 1), WinoLayout.Spacer(),
                    WinoStyle.Label(line.Amount, WinoStyle.Body, WinoStyle.PrimaryText, 1)));
            }
            views.Add(lines);
        }

        if (_model.HasTotal)
        {
            var total = WinoStyle.Label(_model.Total, WinoStyle.Body, WinoStyle.PrimaryText, 1);
            total.Font = UIFont.BoldSystemFontOfSize(17);
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
                    var copy = new UIButton(UIButtonType.System);
                    copy.SetImage(WinoIcons.Image(WinoIconGlyph.Copy, 16, WinoStyle.Accent, Translator.Buttons_Copy), UIControlState.Normal);
                    copy.AccessibilityLabel = Translator.Buttons_Copy;
                    copy.PrimaryActionTriggered += (_, _) => _copyRequested(value);
                    row.AddArrangedSubview(copy);
                }
                fields.AddArrangedSubview(row);
            }
            views.Add(fields);
        }

        if (_model.HasPrimaryAction || _model.HasMoreActions)
            views.Add(BuildActions());

        return views.ToArray();
    }

    private UIStackView BuildActions()
    {
        var actions = WinoLayout.HStack(8);
        actions.Distribution = UIStackViewDistribution.Fill;
        if (_model.PrimaryAction is { } primary)
            actions.AddArrangedSubview(ActionButton(primary, primary.Label, true));
        foreach (var action in _model.MoreActions)
            actions.AddArrangedSubview(ActionButton(action, action.Label, false));
        actions.AddArrangedSubview(WinoLayout.Spacer());
        return actions;
    }

    private UIButton ActionButton(ActionCardAction action, string title, bool primary)
    {
        var button = SettingsBinder.CreateButton(title, primary);
        button.PrimaryActionTriggered += (_, _) => _actionRequested(action);
        return button;
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        _body.Hidden = !_expanded;
        UpdateChevron();
        _expansionChanged(_expanded);
    }

    private void UpdateChevron()
        => _chevron.Image = WinoIcons.Image(_expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, 12, WinoStyle.SecondaryText);
}
