using Android.Content;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Controls.Android.Settings;

/// <summary>How a settings card is shown as a row (owner's mobile settings rule, 15 Oct 2026).</summary>
public enum WinoSettingsRowKind
{
    /// <summary>Title only, not clickable (a status line), with glyph-only buttons kept at the end.</summary>
    Plain,
    /// <summary>Title and a trailing switch; tapping the row toggles it.</summary>
    Switch,
    /// <summary>"Title · current value ›": opens the generic detail page with the real control.</summary>
    Value,
    /// <summary>"Title ›": opens the generic detail page (custom content, several controls, an expander).</summary>
    Push,
    /// <summary>"Title ›": runs the card's own activation (another page, a dialog, a link).</summary>
    Navigate,
    /// <summary>An action row: the title in the primary (or error) colour runs the card's button.</summary>
    Action,
    /// <summary>"Title · value": read-only text.</summary>
    Info
}

/// <summary>
/// The Windows SettingsCard on Android (UIKit and AppKit <c>WinoSettingsCard</c>, same members). It is the description
/// of one setting, not a drawn view: a <see cref="WinoSettingsGroup"/> shows it as a title-only Material list row
/// (<see cref="WinoSettingsRow"/>) chosen from its content (a switch stays inline; pickers, steppers, sliders, text,
/// custom content and button groups push the generic detail page, which hosts <see cref="Content"/> and
/// <see cref="BottomContent"/> with <see cref="Description"/> under them). <see cref="HeaderIcon"/> is kept for the API
/// but not drawn: sub-pages show no icons. <see cref="LeadingView"/> marks an item of a list (an account, a category),
/// whose row keeps its picture and supporting text. Usable from XML as <c>com.winomail.controls.WinoSettingsCard</c>.
/// </summary>
[Register("com.winomail.controls.WinoSettingsCard")]
public class WinoSettingsCard : LinearLayout
{
    private string _header = string.Empty;
    private string? _description;
    private WinoIconGlyph _icon;
    private View? _leading;
    private View? _content;
    private View? _bottom;
    private bool _isClickable;
    private bool _showsChevron;
    private bool _isExpanderHeader;
    private bool _isExpanded;
    private bool _isNested;
    private bool _isDestructive;
    private WinoSettingsRowKind? _rowKind;
    private WinoIconGlyph _actionIcon = WinoIconGlyph.None;

    public WinoSettingsCard(Context context) : base(context) => Initialize();
    public WinoSettingsCard(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize();
    protected WinoSettingsCard(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public WinoSettingsCard(Context context, string header, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None, View? content = null)
        : base(context)
    {
        Initialize();
        _header = header;
        _description = description;
        _icon = icon;
        _content = content;
    }

    private void Initialize() => Orientation = Orientation.Vertical;

    /// <summary>Raised when anything a row shows changes (texts, content, enabled state, visibility).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a clickable card is tapped.</summary>
    public new event EventHandler? Activated;

    /// <summary>Runs the card's activation (a tap on its row).</summary>
    public void Activate()
    {
        if (Enabled) Activated?.Invoke(this, EventArgs.Empty);
    }

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public string Header
    {
        get => _header;
        set { _header = value ?? string.Empty; RaiseChanged(); }
    }

    /// <summary>Shown under the control on the detail page (and under the title of list items); never on a settings page row.</summary>
    public string? Description
    {
        get => _description;
        set { _description = value; RaiseChanged(); }
    }

    /// <summary>Kept for the shared page code; sub-page rows draw no icons.</summary>
    public WinoIconGlyph HeaderIcon
    {
        get => _icon;
        set => _icon = value;
    }

    /// <summary>A picture before the title (an account avatar, a colour chip): the row is a list item and keeps it.</summary>
    public View? LeadingView
    {
        get => _leading;
        set { _leading = value; RaiseChanged(); }
    }

    /// <summary>The card's control (a switch, a dropdown, a stepper, a field, buttons or custom content).</summary>
    public View? Content
    {
        get => _content;
        set { _content = value; RaiseChanged(); }
    }

    /// <summary>A second control under the first (a stepper under a switch, buttons, a preview).</summary>
    public View? BottomContent
    {
        get => _bottom;
        set { _bottom = value; RaiseChanged(); }
    }

    /// <summary>Content never sits beside the text on a row: everything that is not a switch moves to the detail page.</summary>
    public bool IsContentStacked => false;

    /// <summary>The card runs <see cref="Activated"/> when its row is tapped.</summary>
    public bool IsClickable
    {
        get => _isClickable;
        set { _isClickable = value; RaiseChanged(); }
    }

    /// <summary>Kept for the shared page code: every row that opens something shows a chevron.</summary>
    public bool ShowsChevron
    {
        get => _showsChevron;
        set { _showsChevron = value; RaiseChanged(); }
    }

    /// <summary>Kept for the shared page code; nothing expands in place.</summary>
    public bool IsExpanderHeader
    {
        get => _isExpanderHeader;
        set => _isExpanderHeader = value;
    }

    /// <summary>Kept for the shared page code; nothing expands in place.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => _isExpanded = value;
    }

    /// <summary>A trailing glyph in place of the chevron (an external link).</summary>
    public WinoIconGlyph ActionIcon => _actionIcon;

    public void SetActionIcon(WinoIconGlyph glyph)
    {
        _actionIcon = glyph;
        RaiseChanged();
    }

    /// <summary>Kept for the shared page code; rows have no nested surface.</summary>
    public bool IsNested
    {
        get => _isNested;
        set => _isNested = value;
    }

    /// <summary>An action row in the error colour (Delete account, Remove).</summary>
    public bool IsDestructive
    {
        get => _isDestructive;
        set { _isDestructive = value; RaiseChanged(); }
    }

    /// <summary>The row kind when the content alone does not say it; null chooses by rule.</summary>
    public WinoSettingsRowKind? RowKind
    {
        get => _rowKind;
        set { _rowKind = value; RaiseChanged(); }
    }

    /// <summary>Enables or disables the card and its controls; a disabled row is dimmed and ignores taps.</summary>
    public bool IsEnabled
    {
        get => Enabled;
        set => Enabled = value;
    }

    public override bool Enabled
    {
        get => base.Enabled;
        set
        {
            base.Enabled = value;
            SetContentEnabled(value);
            RaiseChanged();
        }
    }

    public override ViewStates Visibility
    {
        get => base.Visibility;
        set
        {
            base.Visibility = value;
            RaiseChanged();
        }
    }

    /// <summary>Enables or disables only the controls.</summary>
    public void SetContentEnabled(bool enabled)
    {
        if (_content is not null) SetEnabledDeep(_content, enabled);
        if (_bottom is not null) SetEnabledDeep(_bottom, enabled);
    }

    private static void SetEnabledDeep(View view, bool enabled)
    {
        view.Enabled = enabled;
        if (view is ViewGroup group)
            for (int index = 0; index < group.ChildCount; index++)
                if (group.GetChildAt(index) is { } child) SetEnabledDeep(child, enabled);
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
