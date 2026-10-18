using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Google.Android.Material.ProgressIndicator;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Shell;

/// <summary>What a <see cref="WinoShellRowView"/> shows.</summary>
public enum WinoShellRowKind
{
    /// <summary>The accent "New mail" / "New event" row at the top of the pane.</summary>
    NewItem,
    /// <summary>An account: its avatar with provider, name and address, the unread pill and the sync bar.</summary>
    Account,
    /// <summary>A folder (or any glyph row): glyph, title and unread pill, indented by depth.</summary>
    Folder,
    /// <summary>A section caption ("Favorites", "More").</summary>
    Header
}

/// <summary>
/// A drawer row for folders and accounts (UIKit <c>WinoShellRowCell</c>, same Configure methods; canvas Material pass
/// Mailboxes). Rows are Material 3 navigation drawer items: 56 dp with 3 dp between neighbours, a full-radius active indicator in the secondary
/// container inset 12 dp from the drawer edges, label-large text. New mail is a tonal (primary container) item.
/// <see cref="ShowsIndicator"/> draws a short accent indicator that marks the active account while a folder below it
/// holds the selection. Sizes come from the wino_m3_drawer_* tokens. Usable from XML as
/// <c>com.winomail.controls.WinoShellRowView</c>.
/// </summary>
[Register("com.winomail.controls.WinoShellRowView")]
public class WinoShellRowView : FrameLayout
{
    private readonly GradientDrawable _fill = new();
    private readonly GradientDrawable _pipe = new();
    private Parts? _parts;
    private WinoShellRowKind _kind = WinoShellRowKind.Folder;
    private bool _isRowSelected;
    private bool _showsIndicator;
    private bool _isCompact;
    private bool? _appliedCompact;

    public WinoShellRowView(Context context) : base(context) => Initialize(context);
    public WinoShellRowView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    protected WinoShellRowView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize(Context context)
    {
        LayoutInflater.From(context)?.Inflate(Resource.Layout.wino_shell_row, this, true);
        _parts = new Parts(this);
        float radius = WinoTokens.PxF(context, Resource.Dimension.wino_m3_drawer_item_radius);
        _fill.SetCornerRadius(radius);
        _parts.Fill.Background = _fill;
        _pipe.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_m3_drawer_indicator_radius));
        _parts.Pipe.Background = _pipe;
        _parts.Glyph.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_drawer_item_glyph);
        _parts.Disclosure.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_drawer_disclosure_glyph);
        _parts.Disclosure.Clickable = true;
        _parts.Disclosure.Click += (_, _) => DisclosureToggled?.Invoke(this, EventArgs.Empty);
        _parts.Account.Size = WinoTokens.Dp(context, Resource.Dimension.wino_m3_drawer_account_icon);
        _parts.Sync.Indeterminate = true;
        Clickable = true;
        Focusable = true;
        Apply();
    }

    private Parts P => _parts ?? throw new InvalidOperationException("The shell row is not initialized.");

    /// <summary>The expand chevron of a folder with children was tapped.</summary>
    public event EventHandler? DisclosureToggled;

    public WinoShellRowKind Kind => _kind;

    /// <summary>The row height token for a kind (Material 3 drawer item or section heading).</summary>
    public static int HeightResource(WinoShellRowKind kind)
        => kind == WinoShellRowKind.Header ? Resource.Dimension.wino_m3_drawer_section_height : Resource.Dimension.wino_m3_drawer_item_height;

    /// <summary>The menu item the page bound; the view never reads it.</summary>
    public object? Item { get; set; }

    public void ConfigureNewItem(string title, WinoIconGlyph glyph = WinoIconGlyph.NewMail)
    {
        Reset(WinoShellRowKind.NewItem);
        P.Glyph.Visibility = ViewStates.Visible;
        P.Glyph.Icon = glyph;
        P.Glyph.Colorful = false;
        P.Title.Text = title;
        ContentDescription = title;
        Apply();
    }

    public void ConfigureAccount(IAccountIconInfo account, string name, string? address = null, int unreadCount = 0, bool isProfilePictureEnabled = true)
    {
        Reset(WinoShellRowKind.Account);
        P.Account.Visibility = ViewStates.Visible;
        P.Account.IsProfilePictureEnabled = isProfilePictureEnabled;
        P.Account.DisplayName = name;
        P.Account.Account = account;
        P.Title.Text = name;
        P.Subtitle.Text = address;
        P.Subtitle.Visibility = string.IsNullOrWhiteSpace(address) ? ViewStates.Gone : ViewStates.Visible;
        P.Pill.Count = unreadCount;
        ContentDescription = string.IsNullOrWhiteSpace(address) ? name : $"{name}, {address}";
        Apply();
    }

    /// <param name="isExpanded">Null for a folder without children; otherwise the chevron shows the state.</param>
    /// <param name="subtleCount">Kept for parity with the UIKit row; Material 3 drawer items always show the count as plain text.</param>
    public void ConfigureFolder(WinoIconGlyph glyph, string title, int unreadCount = 0, int depth = 0, bool? isExpanded = null, bool subtleCount = false)
    {
        Reset(WinoShellRowKind.Folder);
        P.Glyph.Visibility = ViewStates.Visible;
        P.Glyph.Icon = glyph;
        P.Glyph.Colorful = null;
        P.Title.Text = title;
        // Material 3 drawer items show the count as plain label text.
        P.Pill.IsSubtle = true;
        P.Pill.Count = unreadCount;
        P.Disclosure.Visibility = isExpanded is null ? ViewStates.Gone : ViewStates.Visible;
        P.Disclosure.Icon = isExpanded == true ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight;
        // TalkBack: the folder name with its expanded or collapsed state (the glyph alone has no label).
        P.Disclosure.ContentDescription = isExpanded is null ? null : title;
        AndroidX.Core.View.ViewCompat.SetStateDescription(P.Disclosure, isExpanded is null ? null
            : isExpanded == true ? Wino.Core.Domain.Translator.Accessibility_MailThreadExpanded : Wino.Core.Domain.Translator.Accessibility_MailThreadCollapsed);
        var context = this.RequireContext();
        P.Row.SetPaddingRelative(WinoTokens.Px(context, Resource.Dimension.wino_m3_drawer_item_padding_start)
            + Math.Max(0, depth) * WinoTokens.Px(context, Resource.Dimension.wino_m3_drawer_item_depth_indent),
            0, WinoTokens.Px(context, Resource.Dimension.wino_m3_drawer_item_padding_end), 0);
        ContentDescription = unreadCount > 0 ? $"{title}, {unreadCount}" : title;
        Apply();
    }

    public void ConfigureHeader(string title)
    {
        Reset(WinoShellRowKind.Header);
        P.Title.Text = title;
        ContentDescription = title;
        Apply();
    }

    /// <summary>The account sync bar under the address.</summary>
    public void SetSync(bool visible, bool indeterminate = true, double value = 0)
    {
        P.Sync.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (!visible) return;
        P.Sync.Indeterminate = indeterminate;
        if (!indeterminate) P.Sync.SetProgressCompat((int)Math.Round(Math.Clamp(value, 0, 1) * 100), true);
    }

    /// <summary>Selected: the Material 3 active indicator (secondary container).</summary>
    public bool IsRowSelected
    {
        get => _isRowSelected;
        set { _isRowSelected = value; Apply(); }
    }

    /// <summary>Compact: the denser mail folder rows (48 dp, a 44 dp indicator) instead of the 62 dp drawer rows.</summary>
    public bool IsCompact
    {
        get => _isCompact;
        set { _isCompact = value; Apply(); }
    }

    /// <summary>A short accent indicator without the fill (the active account).</summary>
    public bool ShowsIndicator
    {
        get => _showsIndicator;
        set { _showsIndicator = value; Apply(); }
    }

    private void Reset(WinoShellRowKind kind)
    {
        _kind = kind;
        var p = P;
        p.Glyph.Visibility = ViewStates.Gone;
        p.Account.Visibility = ViewStates.Gone;
        p.Subtitle.Visibility = ViewStates.Gone;
        p.Sync.Visibility = ViewStates.Gone;
        p.Disclosure.Visibility = ViewStates.Gone;
        p.Pill.IsSubtle = false;
        p.Pill.Count = 0;
        var context = this.RequireContext();
        p.Row.SetPaddingRelative(WinoTokens.Px(context, kind == WinoShellRowKind.Header ? Resource.Dimension.wino_m3_drawer_section_padding_start : Resource.Dimension.wino_m3_drawer_item_padding_start),
            0, WinoTokens.Px(context, Resource.Dimension.wino_m3_drawer_item_padding_end), 0);
    }

    /// <summary>The indicator, its ripple and the account pipe keep the row's spacing above and below.</summary>
    private void ApplySpacing(Context context, bool compact)
    {
        _appliedCompact = compact;
        int spacing = WinoTokens.Px(context, compact ? Resource.Dimension.wino_m3_drawer_compact_item_spacing : Resource.Dimension.wino_m3_drawer_item_spacing);
        if (P.Fill.LayoutParameters is MarginLayoutParams fill)
        {
            fill.TopMargin = spacing;
            fill.BottomMargin = spacing;
            P.Fill.LayoutParameters = fill;
        }
        float radius = WinoTokens.PxF(context, Resource.Dimension.wino_m3_drawer_item_radius);
        var mask = new GradientDrawable();
        mask.SetColor(Color.White);
        mask.SetCornerRadius(radius);
        int inset = WinoTokens.Px(context, Resource.Dimension.wino_m3_drawer_item_inset);
        Foreground = new RippleDrawable(global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)), null, new InsetDrawable(mask, inset, spacing, inset, spacing));
    }

    private void Apply()
    {
        var p = P;
        var context = this.RequireContext();
        bool compact = _isCompact && _kind == WinoShellRowKind.Folder;
        SetMinimumHeight(WinoTokens.Px(context, compact ? Resource.Dimension.wino_m3_drawer_compact_item_height : HeightResource(_kind)));
        if (_appliedCompact != compact) ApplySpacing(context, compact);
        bool newItem = _kind == WinoShellRowKind.NewItem;
        bool header = _kind == WinoShellRowKind.Header;
        Clickable = !header;
        Focusable = !header;
        var content = newItem ? WinoColors.OnPrimaryContainer(context) : _isRowSelected ? WinoColors.PrimaryText(context) : WinoColors.SecondaryText(context);
        p.Title.SetTextColor(content);
        p.Glyph.SetTextColor(content);
        p.Disclosure.SetTextColor(WinoColors.SecondaryText(context));
        _fill.SetColor(newItem ? WinoColors.PrimaryContainer(context) : _isRowSelected ? WinoColors.SecondaryContainer(context) : Color.Transparent);
        _pipe.SetColor(WinoColors.Accent(context));
        p.Pipe.Visibility = !newItem && !_isRowSelected && _showsIndicator ? ViewStates.Visible : ViewStates.Gone;
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(this, header);
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        WinoColors.AccentChanged += AccentChanged;
        Apply();
    }

    protected override void OnDetachedFromWindow()
    {
        WinoColors.AccentChanged -= AccentChanged;
        base.OnDetachedFromWindow();
    }

    private void AccentChanged(object? sender, EventArgs args) => Apply();

    private sealed class Parts(View root)
    {
        public readonly View Fill = root.Require<View>(Resource.Id.wino_shell_fill);
        public readonly View Pipe = root.Require<View>(Resource.Id.wino_shell_pipe);
        public readonly LinearLayout Row = root.Require<LinearLayout>(Resource.Id.wino_shell_row);
        public readonly WinoIconView Glyph = root.Require<WinoIconView>(Resource.Id.wino_shell_glyph);
        public readonly WinoAccountIconView Account = root.Require<WinoAccountIconView>(Resource.Id.wino_shell_account);
        public readonly TextView Title = root.Require<TextView>(Resource.Id.wino_shell_title);
        public readonly TextView Subtitle = root.Require<TextView>(Resource.Id.wino_shell_subtitle);
        public readonly LinearProgressIndicator Sync = root.Require<LinearProgressIndicator>(Resource.Id.wino_shell_sync);
        public readonly WinoUnreadPill Pill = root.Require<WinoUnreadPill>(Resource.Id.wino_shell_pill);
        public readonly WinoIconView Disclosure = root.Require<WinoIconView>(Resource.Id.wino_shell_disclosure);
    }
}
