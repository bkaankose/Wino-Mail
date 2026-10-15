using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>
/// The Windows SettingsCard on Android (UIKit and AppKit <c>WinoSettingsCard</c>, same members), drawn as a Material 3
/// list item (sizes, surface and stroke from the wino_settings_item_* tokens; plain rows by default) holding an optional glyph or leading view,
/// the header and description, trailing <see cref="Content"/> and an optional chevron, with <see cref="BottomContent"/>
/// below. A clickable card ripples and raises <see cref="Activated"/>. Nested cards (expander rows) drop the card
/// surface. Usable from XML as <c>com.winomail.controls.WinoSettingsCard</c>.
/// </summary>
[Register("com.winomail.controls.WinoSettingsCard")]
public class WinoSettingsCard : LinearLayout
{
    private readonly GradientDrawable _surface = new();
    private Parts? _parts;
    private FrameLayout? _bottom;
    private WinoIconGlyph _icon;
    private bool _isClickable;
    private bool _showsChevron;
    private bool _isExpanderHeader;
    private bool _isExpanded;
    private bool _isNested;
    private WinoIconGlyph _actionIcon = WinoIconGlyph.None;

    public WinoSettingsCard(Context context) : base(context) => Initialize(context);
    public WinoSettingsCard(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    protected WinoSettingsCard(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public WinoSettingsCard(Context context, string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, View? content = null)
        : base(context)
    {
        Initialize(context);
        Header = header;
        Description = description;
        HeaderIcon = icon;
        Content = content;
    }

    private void Initialize(Context context)
    {
        Orientation = Orientation.Vertical;
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_list_item_one_line));
        int horizontal = WinoTokens.Px(context, Resource.Dimension.wino_settings_item_padding_horizontal);
        int vertical = WinoTokens.Px(context, Resource.Dimension.wino_settings_item_padding_vertical);
        row.SetPadding(horizontal, vertical, horizontal, vertical);
        AddView(row, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        LayoutInflater.From(context)?.Inflate(Resource.Layout.wino_settings_card, row, true);
        _parts = new Parts(row);
        _parts.Icon.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_2xl);
        _parts.Action.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_xs);
        _bottom = new FrameLayout(context) { Visibility = ViewStates.Gone };
        _bottom.SetPadding(horizontal, 0, horizontal, vertical);
        AddView(_bottom, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _surface.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_settings_item_radius));
        row.Click += (_, _) => { if (_isClickable && Enabled) Activated?.Invoke(this, EventArgs.Empty); };
        RowView = row;
        ApplySurface();
    }

    private Parts P => _parts ?? throw new InvalidOperationException("The settings card is not initialized.");

    /// <summary>The horizontal header row (the touch target of a clickable card).</summary>
    public LinearLayout? RowView { get; private set; }

    /// <summary>Raised when a clickable card (or an expander header) is tapped.</summary>
    public new event EventHandler? Activated;

    public string Header
    {
        get => P.Header.Text ?? string.Empty;
        set { P.Header.Text = value; UpdateDescription(); }
    }

    public string? Description
    {
        get => P.Description.Text;
        set
        {
            P.Description.Text = value;
            P.Description.Visibility = string.IsNullOrWhiteSpace(value) ? ViewStates.Gone : ViewStates.Visible;
            // Material 3 list items: 56 dp with one line, 72 dp with supporting text.
            RowView?.SetMinimumHeight(WinoTokens.Px(this.RequireContext(), string.IsNullOrWhiteSpace(value)
                ? Resource.Dimension.wino_m3_list_item_one_line : Resource.Dimension.wino_m3_list_item_two_line));
            UpdateDescription();
        }
    }

    public WinoIconGlyph HeaderIcon
    {
        get => _icon;
        set
        {
            _icon = value;
            P.Icon.Icon = value;
            P.Icon.Visibility = value == WinoIconGlyph.None ? ViewStates.Gone : ViewStates.Visible;
            P.Icon.SetTextColor(WinoColors.SecondaryText(this.RequireContext()));
        }
    }

    /// <summary>A view in place of the glyph (an account avatar, a colour swatch).</summary>
    public View? LeadingView
    {
        get => P.Leading.ChildCount > 0 ? P.Leading.GetChildAt(0) : null;
        set => Host(P.Leading, value);
    }

    /// <summary>The trailing control: a switch, dropdown, button or value text.</summary>
    public View? Content
    {
        get => P.Content.ChildCount > 0 ? P.Content.GetChildAt(0) : null;
        set => Host(P.Content, value);
    }

    /// <summary>Content under the header row (a slider, a text field, a list).</summary>
    public View? BottomContent
    {
        get => _bottom is { ChildCount: > 0 } bottom ? bottom.GetChildAt(0) : null;
        set { if (_bottom is not null) Host(_bottom, value); }
    }

    /// <summary>The card ripples and raises <see cref="Activated"/> when tapped.</summary>
    public bool IsClickable
    {
        get => _isClickable;
        set { _isClickable = value; ApplySurface(); }
    }

    /// <summary>A trailing chevron (a card that opens a page).</summary>
    public bool ShowsChevron
    {
        get => _showsChevron;
        set { _showsChevron = value; UpdateAction(); }
    }

    /// <summary>The header of a <see cref="WinoSettingsExpander"/>: a chevron that follows <see cref="IsExpanded"/>.</summary>
    public bool IsExpanderHeader
    {
        get => _isExpanderHeader;
        set { _isExpanderHeader = value; UpdateAction(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; UpdateAction(); }
    }

    /// <summary>A custom trailing glyph (an external link, an open dialog).</summary>
    public void SetActionIcon(WinoIconGlyph glyph)
    {
        _actionIcon = glyph;
        UpdateAction();
    }

    /// <summary>A row inside an expander: no card surface or stroke.</summary>
    public bool IsNested
    {
        get => _isNested;
        set { _isNested = value; ApplySurface(); }
    }

    /// <summary>Enables or disables the card and its content; a disabled card is dimmed.</summary>
    public bool IsEnabled
    {
        get => Enabled;
        set
        {
            Enabled = value;
            Alpha = value ? 1f : 0.45f;
            SetContentEnabled(value);
            if (RowView is not null) RowView.Enabled = value;
        }
    }

    /// <summary>Enables or disables only the trailing and bottom content.</summary>
    public void SetContentEnabled(bool enabled)
    {
        SetEnabledDeep(P.Content, enabled);
        if (_bottom is not null) SetEnabledDeep(_bottom, enabled);
    }

    private static void SetEnabledDeep(View view, bool enabled)
    {
        view.Enabled = enabled;
        if (view is ViewGroup group)
            for (int index = 0; index < group.ChildCount; index++)
                if (group.GetChildAt(index) is { } child) SetEnabledDeep(child, enabled);
    }

    private static void Host(FrameLayout host, View? view)
    {
        host.RemoveAllViews();
        host.Visibility = view is null ? ViewStates.Gone : ViewStates.Visible;
        if (view is null) return;
        (view.Parent as ViewGroup)?.RemoveView(view);
        // A view that brought its own size (a fixed swatch, a full-width field) keeps it; others wrap their content.
        int width = view.LayoutParameters?.Width ?? ViewGroup.LayoutParams.WrapContent;
        int height = view.LayoutParameters?.Height ?? ViewGroup.LayoutParams.WrapContent;
        host.AddView(view, new FrameLayout.LayoutParams(width, height, GravityFlags.CenterVertical | GravityFlags.End));
    }

    private void UpdateAction()
    {
        var glyph = _isExpanderHeader ? (_isExpanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown)
            : _actionIcon != WinoIconGlyph.None ? _actionIcon
            : _showsChevron ? WinoIconGlyph.ChevronRight
            : WinoIconGlyph.None;
        P.Action.Icon = glyph;
        P.Action.Visibility = glyph == WinoIconGlyph.None ? ViewStates.Gone : ViewStates.Visible;
        P.Action.SetTextColor(WinoColors.SecondaryText(this.RequireContext()));
        UpdateDescription();
    }

    private void UpdateDescription()
    {
        if (RowView is null) return;
        RowView.ContentDescription = string.IsNullOrWhiteSpace(P.Description.Text) ? Header : $"{Header}, {P.Description.Text}";
        if (_isExpanderHeader) RowView.StateDescriptionCompat(_isExpanded);
    }

    private void ApplySurface()
    {
        var context = this.RequireContext();
        if (_isNested)
        {
            Background = null;
        }
        else
        {
            _surface.SetColor(WinoTokens.Color(context, Resource.Color.wino_settings_item_fill));
            _surface.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_settings_item_stroke), WinoTokens.Color(context, Resource.Color.wino_settings_item_stroke));
            Background = _surface;
        }
        if (RowView is null) return;
        RowView.Clickable = _isClickable;
        RowView.Focusable = _isClickable;
        if (_isClickable)
        {
            var mask = new GradientDrawable();
            mask.SetColor(WinoTokens.Color(context, Resource.Color.wino_ripple_mask));
            mask.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_settings_item_radius));
            RowView.Foreground = new RippleDrawable(global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)), null, mask);
        }
        else RowView.Foreground = null;
    }

    private sealed class Parts(View root)
    {
        public readonly WinoIconView Icon = root.Require<WinoIconView>(Resource.Id.wino_card_icon);
        public readonly FrameLayout Leading = root.Require<FrameLayout>(Resource.Id.wino_card_leading);
        public readonly TextView Header = root.Require<TextView>(Resource.Id.wino_card_header);
        public readonly TextView Description = root.Require<TextView>(Resource.Id.wino_card_description);
        public readonly FrameLayout Content = root.Require<FrameLayout>(Resource.Id.wino_card_content);
        public readonly WinoIconView Action = root.Require<WinoIconView>(Resource.Id.wino_card_action);
    }
}

internal static class WinoAccessibilityExtensions
{
    /// <summary>"Expanded"/"Collapsed" for TalkBack through the compat state description (API 30 natively).</summary>
    public static void StateDescriptionCompat(this View view, bool expanded)
        => AndroidX.Core.View.ViewCompat.SetStateDescription(view, expanded
            ? global::Wino.Core.Domain.Translator.WinoIntelligence_Expanded
            : global::Wino.Core.Domain.Translator.WinoIntelligence_Collapsed);
}
