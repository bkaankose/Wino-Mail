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
/// The Windows SettingsExpander on Android (UIKit and AppKit <c>WinoSettingsExpander</c>, same members). Nothing expands
/// in place on a phone: a <see cref="WinoSettingsGroup"/> shows it as a "Title ›" row that pushes the generic detail
/// page, where the header's own control comes first and <see cref="Items"/> follow, each rendered by the row rule (a
/// switch stays a switch, pickers become radio sections, steppers and fields are drawn inline). Usable from XML as
/// <c>com.winomail.controls.WinoSettingsExpander</c>.
/// </summary>
[Register("com.winomail.controls.WinoSettingsExpander")]
public class WinoSettingsExpander : LinearLayout
{
    private readonly List<View> _items = [];
    private WinoSettingsCard? _header;
    private bool _isExpanded;

    public WinoSettingsExpander(Context context) : base(context) => Initialize(context, string.Empty, null, WinoIconGlyph.None, null, false);
    public WinoSettingsExpander(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context, string.Empty, null, WinoIconGlyph.None, null, false);
    protected WinoSettingsExpander(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public WinoSettingsExpander(Context context, string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, View? content = null, bool isExpanded = false)
        : base(context) => Initialize(context, header, description, icon, content, isExpanded);

    private void Initialize(Context context, string header, string? description, WinoIconGlyph icon, View? content, bool isExpanded)
    {
        Orientation = Orientation.Vertical;
        _header = new WinoSettingsCard(context, header, description, icon, content) { IsExpanderHeader = true };
        _header.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _isExpanded = isExpanded;
    }

    /// <summary>Raised when the header, the items, the enabled state or the visibility change.</summary>
    public event EventHandler? Changed;

    /// <summary>The header: title, description (shown on the detail page) and its own control.</summary>
    public WinoSettingsCard HeaderCard => _header ?? throw new InvalidOperationException("The expander is not initialized.");

    /// <summary>The nested items in order (cards, expanders or other views).</summary>
    public IReadOnlyList<View> Items => _items;

    /// <summary>Kept for the shared page code; nothing expands in place.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            bool changed = _isExpanded != value;
            _isExpanded = value;
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
            foreach (var item in _items) item.Enabled = value;
        }
    }

    public override ViewStates Visibility
    {
        get => base.Visibility;
        set
        {
            base.Visibility = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Adds a nested item.</summary>
    public void Add(View row)
    {
        _items.Add(row);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a nested item; the margins of the Windows layout do not apply to rows.</summary>
    public void Add(View row, double top, double leading, double bottom, double trailing) => Add(row);

    /// <summary>Removes every nested item.</summary>
    public void ClearItems()
    {
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// A settings section (canvas Android settings boards; UIKit and AppKit <c>WinoSettingsGroup</c>): an optional 12 sp
/// header, a Material card (12 dp corners, 1 dp outline, 8 dp from the zone edge) whose rows are separated by 1 dp
/// lines inset 16 dp, and an optional 13 sp footer under it. Cards and expanders added to it become title-only rows
/// (<see cref="WinoSettingsRow"/>); other views are hosted inside the card with the row insets, and info bars sit above
/// it without a card.
/// </summary>
[Register("com.winomail.controls.WinoSettingsGroup")]
public class WinoSettingsGroup : LinearLayout
{
    private readonly List<View> _sources = [];
    private readonly Dictionary<View, View> _rendered = [];
    private TextView? _title;
    private LinearLayout? _surface;
    private LinearLayout? _above;
    private TextView? _footer;
    private readonly GradientDrawable _card = new();
    private readonly GradientDrawable _divider = new();
    private bool _syncQueued;

    public WinoSettingsGroup(Context context) : base(context) => Initialize(context, null, null);
    public WinoSettingsGroup(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context, null, null);
    protected WinoSettingsGroup(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public WinoSettingsGroup(Context context, string? title = null, string? footer = null) : base(context) => Initialize(context, title, footer);

    private void Initialize(Context context, string? title, string? footer)
    {
        Orientation = Orientation.Vertical;
        // Sections are 16 dp apart, the header (when there is one) inside that gap's end.
        SetPadding(0, WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_section_gap), 0, 0);
        _preDraw = new PreDrawListener(FollowSync);
        _title = WinoTokens.Style(new TextView(context), WinoTextStyle.SettingsSectionHeader);
        WinoTokens.Pad(_title, Resource.Dimension.wino_m3_settings_header_start, 0, Resource.Dimension.wino_m3_settings_header_start, Resource.Dimension.wino_m3_settings_header_bottom);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(_title, true);
        AddView(_title, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Info bars and other notices that belong to the section sit above its card.
        _above = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        AddView(_above, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _surface = new LinearLayout(context) { Orientation = Orientation.Vertical, ClipToOutline = true };
        _card.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_m3_settings_group_radius));
        _surface.Background = _card;
        _divider.SetSize(1, WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_divider));
        _surface.SetDividerDrawable(new InsetDrawable(_divider, WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_divider_inset), 0, 0, 0));
        _surface.ShowDividers = ShowDividers.Middle;
        AddView(_surface, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _footer = WinoTokens.Style(new TextView(context), WinoTextStyle.SettingsFooter);
        WinoSettingsRow.ApplyFooterLineHeight(_footer);
        WinoTokens.Pad(_footer, Resource.Dimension.wino_m3_settings_footer_start, Resource.Dimension.wino_m3_settings_footer_top, Resource.Dimension.wino_m3_settings_footer_start, 0);
        AddView(_footer, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        ApplyColors();
        Title = title;
        Footer = footer;
        UpdateEmpty();
    }

    private void ApplyColors()
    {
        var context = this.RequireContext();
        _card.SetColor(WinoColors.SettingsGroupFill(context));
        _card.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_group_stroke), WinoTokens.Color(context, Resource.Color.wino_settings_group_stroke));
        _divider.SetColor(WinoTokens.Color(context, Resource.Color.wino_settings_row_divider));
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        WinoThemeSurfaces.Changed += ThemeChanged;
        if (_preDraw is not null && (FollowSource is not null || FollowControl is not null)) ViewTreeObserver?.AddOnPreDrawListener(_preDraw);
        ApplyColors();
        FollowSync();
    }

    protected override void OnDetachedFromWindow()
    {
        WinoThemeSurfaces.Changed -= ThemeChanged;
        if (_preDraw is not null) ViewTreeObserver?.RemoveOnPreDrawListener(_preDraw);
        base.OnDetachedFromWindow();
    }

    private void ThemeChanged(object? sender, EventArgs args)
    {
        ApplyColors();
        Invalidate();
    }

    /// <summary>A section of the detail page shows while this card (or expander) is visible and dims while it is disabled.</summary>
    public View? FollowSource { get; set; }

    /// <summary>A section of the detail page shows while this control is visible.</summary>
    public View? FollowControl { get; set; }

    private void FollowSync()
    {
        if (FollowSource is null && FollowControl is null) return;
        bool visible = (FollowSource?.Visibility ?? ViewStates.Visible) == ViewStates.Visible
            && (FollowControl?.Visibility ?? ViewStates.Visible) == ViewStates.Visible;
        bool enabled = FollowSource?.Enabled ?? true;
        var visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (Visibility != visibility) Visibility = visibility;
        if (Enabled != enabled) Enabled = enabled;
        float alpha = enabled ? 1f : WinoColors.Fraction(this.RequireContext(), Resource.Fraction.wino_m3_settings_disabled_alpha);
        if (_surface is not null && Math.Abs(_surface.Alpha - alpha) > 0.001f) _surface.Alpha = alpha;
        OnFollowSync();
    }

    /// <summary>Runs with every follow check (before each frame while attached).</summary>
    protected virtual void OnFollowSync() { }

    private PreDrawListener? _preDraw;

    private sealed class PreDrawListener(Action sync) : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
    {
        public bool OnPreDraw()
        {
            sync();
            return true;
        }
    }

    private LinearLayout RowsHost => _surface ?? throw new InvalidOperationException("The group is not initialized.");

    /// <summary>The card that holds the rows.</summary>
    public ViewGroup Surface => RowsHost;

    /// <summary>The cards, expanders and views added to the section, in order.</summary>
    public IReadOnlyList<View> Sources => _sources;

    public int RowCount => _sources.Count;

    public string? Title
    {
        get => _title?.Text;
        set
        {
            if (_title is null) return;
            _title.Text = value;
            UpdateEmpty();
        }
    }

    public string? Footer
    {
        get => _footer?.Text;
        set
        {
            if (_footer is null) return;
            _footer.Text = value;
            UpdateEmpty();
        }
    }

    public void Clear()
    {
        RowsHost.RemoveAllViews();
        _above?.RemoveAllViews();
        _sources.Clear();
        _rendered.Clear();
        UpdateEmpty();
    }

    /// <summary>Adds a card or expander as a row, or hosts another view in the section.</summary>
    public void Add(View row)
    {
        _sources.Add(row);
        View rendered;
        if (row is WinoSettingsCard or WinoSettingsExpander)
        {
            rendered = WinoSettingsRow.ForPage(this.RequireContext(), row);
            AddRow(rendered);
        }
        else if (row is Common.WinoInfoBar)
        {
            rendered = row;
            (row.Parent as ViewGroup)?.RemoveView(row);
            _above?.AddView(row, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                BottomMargin = WinoTokens.Px(this.RequireContext(), Resource.Dimension.wino_space_s)
            });
        }
        else
        {
            rendered = WinoSettingsRow.Embed(this.RequireContext(), row);
            AddRow(rendered);
        }
        _rendered[row] = rendered;
        UpdateEmpty();
    }

    /// <summary>Hosts a list that draws its own rows (signatures, folders) edge to edge inside the section card.</summary>
    public void AddFlush(View list)
    {
        _sources.Add(list);
        var rendered = WinoSettingsRow.Embed(this.RequireContext(), list, padded: false);
        _rendered[list] = rendered;
        AddRow(rendered);
    }

    public void AddRange(params View[] rows)
    {
        foreach (var row in rows) Add(row);
    }

    /// <summary>Adds an already built row view to the card (the detail page builds its own rows).</summary>
    public void AddRow(View row)
    {
        (row.Parent as ViewGroup)?.RemoveView(row);
        RowsHost.AddView(row, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        UpdateEmpty();
    }

    /// <summary>Hides the header, card and footer while no row is visible; rows call it when they show or hide.</summary>
    public void UpdateEmpty()
    {
        if (_surface is null || _title is null || _footer is null || _above is null) return;
        bool anyRow = false;
        for (int index = 0; index < _surface.ChildCount; index++)
            if (_surface.GetChildAt(index) is { Visibility: ViewStates.Visible }) anyRow = true;
        // Notices manage their own visibility.
        bool anyAbove = _above.ChildCount > 0;
        _surface.Visibility = anyRow ? ViewStates.Visible : ViewStates.Gone;
        _above.Visibility = anyAbove ? ViewStates.Visible : ViewStates.Gone;
        bool any = anyRow || anyAbove;
        // An empty section takes no room (its rows show it again when one of them becomes visible).
        var visibility = any ? ViewStates.Visible : ViewStates.Gone;
        if (_sources.Count > 0 && FollowSource is null && FollowControl is null && Visibility != visibility) Visibility = visibility;
        _title.Visibility = any && !string.IsNullOrWhiteSpace(_title.Text) ? ViewStates.Visible : ViewStates.Gone;
        _footer.Visibility = any && !string.IsNullOrWhiteSpace(_footer.Text) ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>Re-checks the empty state on the next frame (a row's visibility changed during layout).</summary>
    internal void QueueUpdateEmpty()
    {
        if (_syncQueued) return;
        _syncQueued = true;
        Post(() =>
        {
            _syncQueued = false;
            UpdateEmpty();
        });
    }
}
