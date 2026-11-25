using Android.Content;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.ActionCards;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Mail.ActionCards;

/// <summary>
/// One booking or order card above the message (Windows WinoIntelligenceHeader with the action card body): a header with
/// the glyph, title, summary, the primary action and a chevron, and a body with the sections the card's kind fills.
/// The sender's data is shown as read; a button that opens a sender's address goes through the ViewModel's question first.
/// </summary>
public sealed class ReaderActionCardView : LinearLayout
{
    private readonly LinearLayout _body;
    private readonly WinoIconView _chevron;

    public ReaderActionCardView(Context context, ActionCardModel card, bool expanded,
        Action<bool> expansionChanged, Action<ActionCardAction> invoke, Action<string> copy) : base(context)
    {
        Orientation = Orientation.Vertical;
        Background = context.GetDrawable(Resource.Drawable.reader_invitation_background);
        ClipToOutline = true;
        LayoutParameters = new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Px(context, Resource.Dimension.wino_space_m) };

        var header = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true,
            Focusable = true,
            ContentDescription = expanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed,
        };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetMinimumHeight(Px(context, Resource.Dimension.wino_m3_icon_button));
        header.SetPadding(Px(context, Resource.Dimension.wino_space_m), Px(context, Resource.Dimension.wino_space_s), Px(context, Resource.Dimension.wino_space_m), Px(context, Resource.Dimension.wino_space_s));
        var ripple = new global::Android.Util.TypedValue();
        context.Theme?.ResolveAttribute(Resource.Attribute.selectableItemBackground, ripple, true);
        header.SetBackgroundResource(ripple.ResourceId);

        var glyph = new WinoIconView(context) { Icon = card.Glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m) };
        glyph.SetTextColor(WinoColors.AccentText(context));
        header.AddView(glyph);

        var titles = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var title = Lines(new TextView(context) { Text = card.Title, Ellipsize = global::Android.Text.TextUtils.TruncateAt.End }, 1);
        WinoTokens.Style(title, WinoTextStyle.BodyStrong);
        titles.AddView(title);
        if (card.Summary.Length > 0)
        {
            var summary = Lines(new TextView(context) { Text = card.Summary, Ellipsize = global::Android.Text.TextUtils.TruncateAt.End }, 1);
            WinoTokens.Style(summary, WinoTextStyle.Caption);
            titles.AddView(summary);
        }
        header.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { MarginStart = Px(context, Resource.Dimension.wino_space_m), MarginEnd = Px(context, Resource.Dimension.wino_space_s) });

        if (card.PrimaryAction is { } primary)
        {
            var button = SettingsButton(context, primary.Label, primary.IsSenderLink ? WinoIconGlyph.Open : primary.Glyph, outlined: true);
            button.Click += (_, _) => invoke(primary);
            header.AddView(button);
        }

        _chevron = new WinoIconView(context) { Icon = expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m) };
        _chevron.SetTextColor(WinoColors.SecondaryText(context));
        header.AddView(_chevron, new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button), WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button)));
        AddView(header);

        _body = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _body.SetPadding(Px(context, Resource.Dimension.wino_space_m), 0, Px(context, Resource.Dimension.wino_space_m), Px(context, Resource.Dimension.wino_space_m));
        _body.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone;
        FillBody(context, card, invoke, copy);
        AddView(_body);

        header.Click += (_, _) =>
        {
            var nowExpanded = _body.Visibility != ViewStates.Visible;
            SetExpanded(nowExpanded);
            expansionChanged(nowExpanded);
        };
    }

    private void SetExpanded(bool expanded)
    {
        _body.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone;
        _chevron.Icon = expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown;
    }

    private void FillBody(Context context, ActionCardModel card, Action<ActionCardAction> invoke, Action<string> copy)
    {
        if (card.HasHeadline) AddText(context, card.Headline, WinoTextStyle.BodyStrong);
        if (card.HasSubheadline) AddText(context, card.Subheadline, WinoTextStyle.BodySecondary);

        if (card.HasLeg) AddLeg(context, card);

        if (card.HasAmount)
        {
            AddText(context, card.AmountLabel, WinoTextStyle.Caption, WinoColors.SecondaryText(context));
            AddText(context, card.Amount, WinoTextStyle.BodyLargeStrong);
        }

        if (card.HasLines)
        {
            foreach (var line in card.Lines) _body.AddView(LineRow(context, line));
            if (card.HasTotal) AddText(context, card.Total, WinoTextStyle.BodyStrong, gravity: GravityFlags.End);
        }

        if (card.HasSteps) _body.AddView(StepRow(context, card.Steps));

        foreach (var field in card.Fields) _body.AddView(FieldRow(context, field, copy));

        if (card.HasMoreActions)
        {
            var actions = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            foreach (var action in card.MoreActions)
            {
                var button = SettingsButton(context, action.Label, action.Glyph, outlined: true);
                button.Click += (_, _) => invoke(action);
                actions.AddView(button, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = Px(context, Resource.Dimension.wino_space_s) });
            }
            var scroll = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false };
            scroll.AddView(actions);
            _body.AddView(scroll);
        }

        AddText(context, Translator.ActionCard_SenderDataNote, WinoTextStyle.Caption, WinoColors.TertiaryText(context));
    }

    private void AddLeg(Context context, ActionCardModel card)
    {
        var leg = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        leg.SetGravity(GravityFlags.CenterVertical);
        leg.AddView(Column(context, card.DepartureTime, card.DeparturePlace, GravityFlags.Start), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        leg.AddView(new WinoIconView(context) { Icon = card.Glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m) }, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        leg.AddView(Column(context, card.ArrivalTime, card.ArrivalPlace, GravityFlags.End), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _body.AddView(leg, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Px(context, Resource.Dimension.wino_space_s) });
    }

    private static LinearLayout Column(Context context, string time, string place, GravityFlags gravity)
    {
        var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var timeView = new TextView(context) { Text = time, Gravity = gravity };
        WinoTokens.Style(timeView, WinoTextStyle.Title);
        column.AddView(timeView);
        var placeView = new TextView(context) { Text = place, Gravity = gravity };
        WinoTokens.Style(placeView, WinoTextStyle.Caption);
        column.AddView(placeView);
        return column;
    }

    private static LinearLayout LineRow(Context context, ActionCardLine line)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, Px(context, Resource.Dimension.wino_space_xs), 0, Px(context, Resource.Dimension.wino_space_xs));
        var text = Lines(new TextView(context) { Text = line.Text, Ellipsize = global::Android.Text.TextUtils.TruncateAt.End }, 2);
        WinoTokens.Style(text, WinoTextStyle.Body);
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { MarginEnd = Px(context, Resource.Dimension.wino_space_m) });
        var amount = new TextView(context) { Text = line.Amount };
        WinoTokens.Style(amount, WinoTextStyle.Body);
        row.AddView(amount);
        return row;
    }

    private static LinearLayout StepRow(Context context, IReadOnlyList<ActionCardStep> steps)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        foreach (var step in steps)
        {
            var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
            var bar = new View(context) { Background = StepBar(context, step.IsReached ? WinoColors.AccentText(context) : WinoColors.Outline(context)) };
            column.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Px(context, Resource.Dimension.wino_space_xs)));
            var label = Lines(new TextView(context) { Text = step.Label, Ellipsize = global::Android.Text.TextUtils.TruncateAt.End }, 1);
            WinoTokens.Style(label, WinoTextStyle.Caption);
            column.AddView(label);
            row.AddView(column, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { MarginEnd = Px(context, Resource.Dimension.wino_space_s), TopMargin = Px(context, Resource.Dimension.wino_space_s) });
        }
        return row;
    }

    private static LinearLayout FieldRow(Context context, ActionCardField field, Action<string> copy)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Vertical };
        row.SetPadding(0, Px(context, Resource.Dimension.wino_space_s), 0, 0);
        var label = new TextView(context) { Text = field.Label };
        WinoTokens.Style(label, WinoTextStyle.Caption);
        label.SetTextColor(WinoColors.SecondaryText(context));
        row.AddView(label);

        var value = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        value.SetGravity(GravityFlags.CenterVertical);
        var text = new TextView(context) { Text = field.Value };
        text.SetTextIsSelectable(true);
        WinoTokens.Style(text, WinoTextStyle.BodyStrong);
        value.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (field.IsCopyable)
        {
            var copyButton = new WinoIconView(context) { Icon = WinoIconGlyph.Copy, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_s), ContentDescription = Translator.ActionCard_CopyValueFormat.Replace("{0}", field.Label, StringComparison.Ordinal) };
            copyButton.SetTextColor(WinoColors.SecondaryText(context));
            copyButton.Clickable = true;
            copyButton.Click += (_, _) => copy(field.Value);
            value.AddView(copyButton, new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button), WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button)));
        }
        row.AddView(value);
        return row;
    }

    private void AddText(Context context, string text, WinoTextStyle style, global::Android.Graphics.Color? color = null, GravityFlags gravity = GravityFlags.Start)
    {
        if (text.Length == 0) return;
        var view = new TextView(context) { Text = text, Gravity = gravity };
        WinoTokens.Style(view, style);
        if (color is { } tint) view.SetTextColor(tint);
        _body.AddView(view, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Px(context, Resource.Dimension.wino_space_xs) });
    }

    private static MaterialButton SettingsButton(Context context, string text, WinoIconGlyph icon, bool outlined)
    {
        var button = Wino.Mail.Controls.Android.Settings.SettingsBinder.CreateButton(context, text, outlined, icon);
        button.TextSize = 13;
        return button;
    }

    private static global::Android.Graphics.Drawables.GradientDrawable StepBar(Context context, global::Android.Graphics.Color color)
    {
        var bar = new global::Android.Graphics.Drawables.GradientDrawable();
        bar.SetColor(color);
        bar.SetCornerRadius(Px(context, Resource.Dimension.wino_space_xs));
        return bar;
    }

    private static int Px(Context context, int dimension) => WinoTokens.Px(context, dimension);

    private static TextView Lines(TextView view, int lines)
    {
        view.SetMaxLines(lines);
        return view;
    }
}
