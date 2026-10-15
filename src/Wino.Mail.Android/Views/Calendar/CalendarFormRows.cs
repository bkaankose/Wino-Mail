using Android.Content;
using Android.Views;
using Android.Widget;
using Wino.Presentation.Android;
using Wino.Presentation.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// One row of the event details and the event editor (canvas Material pass board EventEditor): a leading WinoIcons
/// glyph in a 56 dp gutter, a primary line and an optional supporting line, an optional trailing view, and a ripple when
/// it is tappable. Sizes and text styles are tokens.
/// </summary>
internal sealed class CalendarFormRow : LinearLayout
{
    private readonly WinoIconView _glyph;
    private readonly TextView _text;
    private readonly TextView _supporting;
    private readonly FrameLayout _trailing;

    public CalendarFormRow(Context context, WinoIconGlyph glyph, bool compact = false) : base(context)
    {
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        SetMinimumHeight(WinoTokens.Px(context, compact ? Resource.Dimension.wino_calendar_editor_sub_row_height : Resource.Dimension.wino_calendar_editor_row_height));
        int padding = WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        SetPadding(padding, WinoTokens.Px(context, Resource.Dimension.wino_space_2xs), padding, WinoTokens.Px(context, Resource.Dimension.wino_space_2xs));

        _glyph = new WinoIconView(context) { Icon = glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), Gravity = GravityFlags.CenterVertical | GravityFlags.Start, ImportantForAccessibility = ImportantForAccessibility.No };
        _glyph.SetTextColor(WinoColors.SecondaryText(context));
        int gutter = WinoTokens.Px(context, Resource.Dimension.wino_calendar_editor_row_indent) - padding;
        AddView(_glyph, new LayoutParams(gutter, ViewGroup.LayoutParams.WrapContent));

        var texts = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _text = WinoTokens.Style(new TextView(context), WinoTextStyle.BodyLarge);
        _supporting = WinoTokens.Style(new TextView(context) { Visibility = ViewStates.Gone }, WinoTextStyle.BodySecondary);
        texts.AddView(_text);
        texts.AddView(_supporting);
        AddView(texts, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _trailing = new FrameLayout(context) { Visibility = ViewStates.Gone };
        AddView(_trailing, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
    }

    public string? Text
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    public string? Supporting
    {
        get => _supporting.Text;
        set
        {
            _supporting.Text = value;
            _supporting.Visibility = string.IsNullOrWhiteSpace(value) ? ViewStates.Gone : ViewStates.Visible;
        }
    }

    /// <summary>Placeholder look (secondary text) for rows that invite input ("Add description").</summary>
    public bool IsPlaceholder
    {
        set => _text.SetTextColor(value ? WinoColors.SecondaryText(Context!) : WinoColors.PrimaryText(Context!));
    }

    public WinoIconGlyph Glyph
    {
        set => _glyph.Icon = value;
    }

    /// <summary>Hides the glyph but keeps its gutter (follow-up rows of a group).</summary>
    public bool ShowsGlyph
    {
        set => _glyph.Visibility = value ? ViewStates.Visible : ViewStates.Invisible;
    }

    public void SetTrailing(View? view)
    {
        _trailing.RemoveAllViews();
        if (view is not null) _trailing.AddView(view);
        _trailing.Visibility = view is null ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>Makes the row a button.</summary>
    public void OnTap(Action action)
    {
        SetBackgroundResource(Shell.ShellViews.Ripple(Context!));
        Click += (_, _) => action();
    }

    /// <summary>A full-width divider between groups (the editor's 1 dp rule with 16 dp insets).</summary>
    public static View Divider(Context context)
    {
        var divider = new View(context);
        divider.SetBackgroundColor(WinoTokens.Color(context, Resource.Color.wino_calendar_grid_line));
        int inset = WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        divider.LayoutParameters = new LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_calendar_grid_stroke))
        {
            MarginStart = inset,
            MarginEnd = inset,
            TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_s),
            BottomMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_s)
        };
        return divider;
    }
}
