using Android.Content;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>
/// The Windows SettingsExpander on Android (UIKit and AppKit <c>WinoSettingsExpander</c>, same members; canvas
/// Components "Swipe actions" expander): a header card with a chevron and, while expanded, nested rows under a divider,
/// indented to the header text. Tapping the header toggles it. Usable from XML as
/// <c>com.winomail.controls.WinoSettingsExpander</c>.
/// </summary>
[Register("com.winomail.controls.WinoSettingsExpander")]
public class WinoSettingsExpander : LinearLayout
{
    private readonly GradientDrawable _surface = new();
    private WinoSettingsCard? _header;
    private LinearLayout? _rows;
    private bool _isExpanded;

    public WinoSettingsExpander(Context context) : base(context) => Initialize(context, string.Empty, null, WinoIconGlyph.None, null, false);
    public WinoSettingsExpander(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context, string.Empty, null, WinoIconGlyph.None, null, false);
    protected WinoSettingsExpander(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public WinoSettingsExpander(Context context, string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, View? content = null, bool isExpanded = false)
        : base(context) => Initialize(context, header, description, icon, content, isExpanded);

    private void Initialize(Context context, string header, string? description, WinoIconGlyph icon, View? content, bool isExpanded)
    {
        Orientation = Orientation.Vertical;
        _surface.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_settings_item_radius));
        _surface.SetColor(WinoTokens.Color(context, Resource.Color.wino_settings_item_fill));
        _surface.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_settings_item_stroke), WinoTokens.Color(context, Resource.Color.wino_settings_item_stroke));
        Background = _surface;
        ClipToOutline = true;

        _header = new WinoSettingsCard(context, header, description, icon, content) { IsNested = true, IsExpanderHeader = true, IsClickable = true };
        _header.Activated += (_, _) => IsExpanded = !IsExpanded;
        AddView(_header, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _rows = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var divider = new GradientDrawable();
        divider.SetColor(WinoColors.Divider(context));
        divider.SetSize(1, WinoTokens.Px(context, Resource.Dimension.wino_stroke_hairline));
        _rows.SetDividerDrawable(divider);
        _rows.ShowDividers = ShowDividers.Beginning;
        _rows.SetPadding(WinoTokens.Px(context, Resource.Dimension.wino_settings_nested_indent), 0,
            WinoTokens.Px(context, Resource.Dimension.wino_settings_item_padding_horizontal), WinoTokens.Px(context, Resource.Dimension.wino_space_s));
        AddView(_rows, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        IsExpanded = isExpanded;
    }

    public WinoSettingsCard HeaderCard => _header ?? throw new InvalidOperationException("The expander is not initialized.");

    private LinearLayout Rows => _rows ?? throw new InvalidOperationException("The expander is not initialized.");

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            bool changed = _isExpanded != value;
            _isExpanded = value;
            HeaderCard.IsExpanded = value;
            Rows.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
            if (changed) IsExpandedChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? IsExpandedChanged;

    public bool IsEnabled
    {
        get => HeaderCard.IsEnabled;
        set
        {
            HeaderCard.IsEnabled = value;
            Rows.Alpha = value ? 1f : 0.45f;
            for (int index = 0; index < Rows.ChildCount; index++)
                if (Rows.GetChildAt(index) is { } child) child.Enabled = value;
        }
    }

    /// <summary>Adds a nested row (a nested <see cref="WinoSettingsCard"/> or any view) with an 8 dp top gap.</summary>
    public void Add(View row) => Add(row, 8, 0, 0, 0);

    /// <summary>Adds a nested row with margins in dp.</summary>
    public void Add(View row, double top, double leading, double bottom, double trailing)
    {
        var context = this.RequireContext();
        if (row is WinoSettingsCard card) card.IsNested = true;
        var layout = new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = WinoUnits.Dp(context, top),
            BottomMargin = WinoUnits.Dp(context, bottom)
        };
        layout.MarginStart = WinoUnits.Dp(context, leading);
        layout.MarginEnd = WinoUnits.Dp(context, trailing);
        Rows.AddView(row, layout);
    }
}

/// <summary>
/// A titled settings group (UIKit and AppKit <c>WinoSettingsGroup</c>; canvas Settings: 24 dp group headers, cards with
/// 4 dp gaps): the caption, the cards and an optional footer.
/// </summary>
[Register("com.winomail.controls.WinoSettingsGroup")]
public class WinoSettingsGroup : LinearLayout
{
    private TextView? _title;
    private LinearLayout? _rows;
    private TextView? _footer;

    public WinoSettingsGroup(Context context) : base(context) => Initialize(context, null, null);
    public WinoSettingsGroup(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context, null, null);
    protected WinoSettingsGroup(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public WinoSettingsGroup(Context context, string? title = null, string? footer = null) : base(context) => Initialize(context, title, footer);

    private void Initialize(Context context, string? title, string? footer)
    {
        Orientation = Orientation.Vertical;
        // Material 3 list subheader in the primary colour (the theme accent).
        _title = WinoTokens.Style(new TextView(context), WinoTextStyle.Subheader);
        _title.SetTextColor(WinoColors.AccentText(context));
        _title.SetPadding(WinoTokens.Px(context, Resource.Dimension.wino_settings_subheader_horizontal), WinoTokens.Px(context, Resource.Dimension.wino_settings_subheader_top),
            WinoTokens.Px(context, Resource.Dimension.wino_settings_subheader_horizontal), WinoTokens.Px(context, Resource.Dimension.wino_settings_subheader_bottom));
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(_title, true);
        AddView(_title);

        _rows = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var gap = new GradientDrawable();
        gap.SetSize(1, WinoTokens.Px(context, Resource.Dimension.wino_settings_item_spacing));
        _rows.SetDividerDrawable(gap);
        _rows.ShowDividers = ShowDividers.Middle;
        AddView(_rows, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _footer = new TextView(context);
        WinoTokens.Style(_footer, WinoTextStyle.Caption);
        _footer.SetPadding(WinoTokens.Px(context, Resource.Dimension.wino_settings_subheader_horizontal), WinoTokens.Px(context, Resource.Dimension.wino_space_xs),
            WinoTokens.Px(context, Resource.Dimension.wino_settings_subheader_horizontal), 0);
        AddView(_footer);
        Title = title;
        Footer = footer;
    }

    private LinearLayout RowsHost => _rows ?? throw new InvalidOperationException("The group is not initialized.");

    /// <summary>The container that holds the cards.</summary>
    public ViewGroup Surface => RowsHost;

    public int RowCount => RowsHost.ChildCount;

    public string? Title
    {
        get => _title?.Text;
        set
        {
            if (_title is null) return;
            _title.Text = value;
            _title.Visibility = string.IsNullOrWhiteSpace(value) ? ViewStates.Gone : ViewStates.Visible;
        }
    }

    public string? Footer
    {
        get => _footer?.Text;
        set
        {
            if (_footer is null) return;
            _footer.Text = value;
            _footer.Visibility = string.IsNullOrWhiteSpace(value) ? ViewStates.Gone : ViewStates.Visible;
        }
    }

    public void Clear() => RowsHost.RemoveAllViews();

    public void Add(View row) => RowsHost.AddView(row, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

    public void AddRange(params View[] rows)
    {
        foreach (var row in rows) Add(row);
    }
}
