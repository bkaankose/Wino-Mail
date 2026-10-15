using Android.Content;
using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// One selectable offer (Windows BenefitTileTemplate, iOS WinoAccountBenefitTileView): the glyph in a rounded
/// box, the title with its Free / Add-on badge, and the caption. A tap selects it; TalkBack reads it as a selectable
/// button with the caption.
/// </summary>
internal sealed class WinoAccountBenefitTileView : LinearLayout
{
    private bool _isSelected;

    public WinoAccountBenefitTileView(Context context, WinoAccountBenefitItemViewModel benefit) : base(context)
    {
        Benefit = benefit;
        Orientation = Orientation.Horizontal;
        WinoTokens.Pad(this, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l);
        Clickable = true;
        Focusable = true;

        var glyph = new WinoIconView(context) { Icon = benefit.Icon, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_l) };
        glyph.SetTextColor(WinoColors.PrimaryText(context));
        var box = new FrameLayout(context) { Background = IntelligenceViews.Shape(context, WinoColors.SubtleFill(context), WinoColors.CardStroke(context), Resource.Dimension.wino_radius_large) };
        box.AddView(glyph, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        int boxSize = WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_benefit_icon_box);
        AddView(box, new LayoutParams(boxSize, boxSize));

        var title = IntelligenceViews.Label(context, benefit.Title, WinoTextStyle.BodyStrong);
        // Free and paid badges differ only in tint (Windows: success stroke for free, card stroke for add-ons).
        var badge = IntelligenceViews.Pill(context, benefit.BadgeText,
            benefit.IsFreeBadge ? WinoColors.Success(context) : WinoColors.SecondaryText(context),
            WinoColors.SubtleFill(context),
            benefit.IsFreeBadge ? WinoColors.Success(context) : WinoColors.CardStroke(context));
        WinoTokens.Pad(badge, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_hairline, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_hairline);
        var heading = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        heading.SetGravity(GravityFlags.CenterVertical);
        heading.AddView(title, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 0) { Weight = 0 });
        heading.AddView(badge, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
        var caption = IntelligenceViews.Label(context, benefit.Caption, WinoTextStyle.Description);
        var text = IntelligenceViews.VStack(context, Resource.Dimension.wino_space_3xs, heading, caption);
        AddView(text, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_ml) });

        ContentDescription = $"{benefit.Title}, {benefit.BadgeText}. {benefit.Caption}";
        ImportantForAccessibility = ImportantForAccessibility.Yes;
        for (int index = 0; index < ChildCount; index++)
            if (GetChildAt(index) is { } child) child.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
        Click += (_, _) => Selected?.Invoke(this, EventArgs.Empty);
        Update();
    }


    public WinoAccountBenefitItemViewModel Benefit { get; }

    /// <summary>Raised when the user picks this offer.</summary>
    public new event EventHandler? Selected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            Update();
        }
    }

    private void Update()
    {
        if (Context is not { } context) return;
        var fill = _isSelected ? WinoColors.SelectedFill(context) : WinoColors.CardFill(context);
        var stroke = _isSelected ? WinoColors.Accent(context) : WinoColors.CardStroke(context);
        Background = new global::Android.Graphics.Drawables.RippleDrawable(
            global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)),
            IntelligenceViews.Shape(context, fill, stroke, Resource.Dimension.wino_radius_small,
                _isSelected ? Resource.Dimension.wino_winoaccount_stroke_selected : Resource.Dimension.wino_winoaccount_stroke), null);
        Activated = _isSelected;
    }

    public override void OnInitializeAccessibilityNodeInfo(AccessibilityNodeInfo? info)
    {
        base.OnInitializeAccessibilityNodeInfo(info);
        if (info is null) return;
        info.ClassName = "android.widget.Button";
        info.Selected = _isSelected;
    }
}

/// <summary>
/// Detail for the selected offer (Windows WinoAccountBenefitDetailPanel, iOS twin): the offer's illustration in a
/// column beside the copy when the panel is wide enough (tablets), otherwise the copy alone (every
/// phone width), then the title, lede, what it includes and the call to action.
/// </summary>
internal sealed class WinoAccountBenefitDetailPanel : LinearLayout
{
    private readonly FrameLayout _art;
    private readonly View _divider;
    private readonly WinoAccountBenefitIllustrationView _illustration;
    private readonly TextView _title;
    private readonly TextView _lede;
    private readonly LinearLayout _points;

    public WinoAccountBenefitDetailPanel(Context context) : base(context)
    {
        Orientation = Orientation.Horizontal;
        Background = IntelligenceViews.Shape(context, WinoColors.CardFill(context), WinoColors.CardStroke(context), Resource.Dimension.wino_radius_large);
        ClipToOutline = true;

        _art = new FrameLayout(context) { Background = IntelligenceViews.Shape(context, WinoColors.SubtleFill(context), null, Resource.Dimension.wino_radius_large), Visibility = ViewStates.Gone };
        WinoTokens.Pad(_art, Resource.Dimension.wino_space_2xl, Resource.Dimension.wino_space_2xl, Resource.Dimension.wino_space_2xl, Resource.Dimension.wino_space_2xl);
        _illustration = new WinoAccountBenefitIllustrationView(context);
        _art.AddView(_illustration, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterVertical));
        AddView(_art, new LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_benefit_art_width), ViewGroup.LayoutParams.MatchParent));
        _divider = new View(context) { Visibility = ViewStates.Gone };
        _divider.SetBackgroundColor(WinoColors.CardStroke(context));
        AddView(_divider, new LayoutParams(Math.Max(1, WinoTokens.Px(context, Resource.Dimension.wino_space_hairline)), ViewGroup.LayoutParams.MatchParent));

        _title = IntelligenceViews.Label(context, string.Empty, WinoTextStyle.Title);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) _title.AccessibilityHeading = true;
        _lede = IntelligenceViews.Label(context, string.Empty, WinoTextStyle.BodySecondary);
        _points = IntelligenceViews.VStack(context, Resource.Dimension.wino_space_s);
        CallToAction = SettingsBinder.CreateButton(context, string.Empty, outlined: false);
        var ctaRow = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        ctaRow.AddView(CallToAction, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        var copy = IntelligenceViews.VStack(context, Resource.Dimension.wino_space_ml, _title, _lede, _points);
        IntelligenceViews.Append(copy, ctaRow, Resource.Dimension.wino_space_l);
        WinoTokens.Pad(copy, Resource.Dimension.wino_space_xl, Resource.Dimension.wino_space_xl, Resource.Dimension.wino_space_xl, Resource.Dimension.wino_space_xl);
        AddView(copy, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
    }


    public MaterialButton CallToAction { get; }

    public void Show(WinoAccountBenefitItemViewModel? benefit)
    {
        if (Context is not { } context) return;
        _title.Text = benefit?.Title ?? string.Empty;
        _lede.Text = benefit?.Lede ?? string.Empty;
        CallToAction.Text = benefit?.CtaText ?? string.Empty;
        CallToAction.Visibility = benefit is null ? ViewStates.Gone : ViewStates.Visible;
        _illustration.Kind = benefit?.Type;
        _points.RemoveAllViews();
        foreach (var point in benefit?.Points ?? [])
        {
            var check = new WinoIconView(context) { Icon = WinoIconGlyph.Checkmark, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_s) };
            check.SetTextColor(WinoColors.Accent(context));
            var label = IntelligenceViews.Label(context, point, WinoTextStyle.BodySecondary);
            var line = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            int checkSize = WinoTokens.Px(context, Resource.Dimension.wino_glyph_m);
            line.AddView(check, new LayoutParams(checkSize, checkSize) { TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_3xs) });
            line.AddView(label, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_sm) });
            IntelligenceViews.Append(_points, line, Resource.Dimension.wino_space_s);
        }
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        if (Context is { } context)
        {
            // Below this width the illustration would squeeze the copy, so it gives up its column.
            var hide = MeasureSpec.GetSize(widthMeasureSpec) < WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_benefit_art_min_panel);
            var state = hide ? ViewStates.Gone : ViewStates.Visible;
            _art.Visibility = state;
            _divider.Visibility = state;
        }
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
    }
}
