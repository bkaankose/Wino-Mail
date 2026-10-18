using System.Windows.Input;
using Android.Content;
using ColorStateList = Android.Content.Res.ColorStateList;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>One entry of a row's actions menu (the iOS UIMenu actions).</summary>
internal sealed record SettingsMenuEntry(string Title, Action Invoke, bool IsDestructive = false, bool IsChecked = false);

/// <summary>
/// Small pieces shared by the account sub-pages' custom rows (iOS SettingsRowParts, SettingsGlyphButton,
/// SettingsPillButton, DashedDropZoneView and MailFilterPresentation): surfaces, tags, 48 dp glyph toggles, accent pill
/// buttons, the row actions menu and the filter tone tiles.
/// </summary>
internal static class AccountSettingsParts
{
    /// <summary>Raised row surface inside an expander (white in light, a faint lift in dark).</summary>
    public static Color RowFill(Context context) => WinoTokens.Color(context, Resource.Color.wino_raised_fill);

    /// <summary>A rounded rectangle with an optional (dashed) stroke; radius and stroke width are dimension resources.</summary>
    public static GradientDrawable Surface(Context context, Color fill, Color? stroke = null, int radius = Resource.Dimension.wino_radius_small, bool dashed = false,
        int strokeWidth = Resource.Dimension.wino_space_hairline)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(WinoTokens.PxF(context, radius));
        if (stroke is { } color)
        {
            var width = WinoTokens.Px(context, strokeWidth);
            if (dashed)
                drawable.SetStroke(width, color, WinoTokens.PxF(context, Resource.Dimension.wino_accounts_dash_length), WinoTokens.PxF(context, Resource.Dimension.wino_accounts_dash_gap));
            else drawable.SetStroke(width, color);
        }
        return drawable;
    }

    /// <summary>The dashed outline of an empty list (iOS DashedDropZoneView).</summary>
    public static GradientDrawable DashedOutline(Context context, Color? fill = null)
        => Surface(context, fill ?? Color.Transparent, WinoTokens.Color(context, Resource.Color.wino_accounts_dashed_stroke), Resource.Dimension.wino_radius_medium, dashed: true);

    /// <summary>A text view in a Wino text appearance; <paramref name="color"/> overrides the appearance's colour.</summary>
    public static TextView Label(Context context, string? text, WinoTextStyle style, Color? color = null, int maxLines = 0)
    {
        var label = WinoTokens.Style(new TextView(context) { Text = text ?? string.Empty }, style);
        if (color is { } value) label.SetTextColor(value);
        if (maxLines > 0)
        {
            label.SetMaxLines(maxLines);
            label.Ellipsize = TextUtils.TruncateAt.End;
        }
        return label;
    }

    /// <summary>A rounded caption tag ("System", "Hidden").</summary>
    public static TextView Tag(Context context, string text)
    {
        var label = Label(context, text, WinoTextStyle.CaptionStrong, maxLines: 1);
        WinoTokens.Pad(label, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_3xs, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_3xs);
        label.Background = Surface(context, WinoColors.SelectedFill(context), null, Resource.Dimension.wino_radius_pill);
        return label;
    }

    /// <summary>The accent "Default" capsule.</summary>
    public static TextView AccentPill(Context context, string text)
    {
        var label = Label(context, text, WinoTextStyle.CaptionStrong, WinoColors.AccentText(context), maxLines: 1);
        WinoTokens.Pad(label, Resource.Dimension.wino_space_xs, Resource.Dimension.wino_space_hairline, Resource.Dimension.wino_space_xs, Resource.Dimension.wino_space_hairline);
        label.Background = Surface(context, WinoColors.WithAlpha(WinoColors.Accent(context), 0.12), null, Resource.Dimension.wino_radius_pill);
        label.ImportantForAccessibility = ImportantForAccessibility.No;
        return label;
    }

    /// <summary>
    /// A 48 dp borderless glyph button with an optional pressed (toggled) wash: the Windows subtle ToggleButton/Button in
    /// the folder and filter rows. The accessibility label is required.
    /// </summary>
    public static MaterialButton GlyphButton(Context context, WinoIconGlyph glyph, string label, bool toggled = false)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.materialIconButtonStyle)
        {
            ContentDescription = label,
            IconPadding = 0,
            IconGravity = MaterialButton.IconGravityTextStart
        };
        button.InsetTop = 0;
        button.InsetBottom = 0;
        button.CornerRadius = WinoTokens.Px(context, Resource.Dimension.wino_radius_large);
        var touch = WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large);
        button.SetMinWidth(touch);
        button.SetMinimumWidth(touch);
        button.SetMinHeight(touch);
        button.SetMinimumHeight(touch);
        SetGlyph(context, button, glyph, toggled);
        return button;
    }

    /// <summary>Applies the glyph and the toggled state (wash and primary tint) to a <see cref="GlyphButton"/>.</summary>
    public static void SetGlyph(Context context, MaterialButton button, WinoIconGlyph glyph, bool toggled)
    {
        var tint = toggled ? WinoColors.PrimaryText(context) : WinoColors.SecondaryText(context);
        button.Icon = WinoIcons.Drawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_ml), tint);
        button.IconTint = ColorStateList.ValueOf(tint);
        var wash = toggled ? WinoTokens.Color(context, Resource.Color.wino_toggle_wash) : Color.Transparent;
        button.BackgroundTintList = ColorStateList.ValueOf(wash);
        button.Selected = toggled;
    }

    /// <summary>Small accent-text button on an accent wash ("Link", "Unlink").</summary>
    public static MaterialButton PillButton(Context context, string title, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var button = SettingsBinder.CreateButton(context, title, outlined: true);
        var accent = WinoColors.AccentText(context);
        button.StrokeWidth = 0;
        button.BackgroundTintList = ColorStateList.ValueOf(WinoColors.WithAlpha(WinoColors.Accent(context), 0.1));
        button.SetTextColor(accent);
        if (icon != WinoIconGlyph.None)
        {
            button.Icon = WinoIcons.Drawable(context, icon, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_s), accent);
            button.IconTint = ColorStateList.ValueOf(accent);
            button.IconPadding = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs);
        }
        var touch = WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large);
        button.SetMinHeight(touch);
        button.SetMinimumHeight(touch);
        return button;
    }

    /// <summary>Gives a button the critical text colour (destructive actions).</summary>
    public static MaterialButton Destructive(Context context, MaterialButton button)
    {
        var critical = WinoColors.Critical(context);
        button.SetTextColor(critical);
        button.StrokeColor = ColorStateList.ValueOf(WinoColors.WithAlpha(critical, 0.5));
        // Its action row on a settings page uses the error colour.
        return WinoSettingsControls.MarkDestructive(button);
    }

    /// <summary>The 48 dp "More" button whose popup menu lists <paramref name="entries"/> (the iOS row actions menu).</summary>
    public static MaterialButton MoreButton(Context context, SettingsBinder binder, string label, IReadOnlyList<SettingsMenuEntry> entries)
    {
        var button = GlyphButton(context, WinoIconGlyph.More, label);
        binder.Views.Click(button, () => ShowMenu(context, button, entries, binder.Views.Error));
        return button;
    }

    /// <summary>Shows a popup menu under <paramref name="anchor"/>; destructive entries are red.</summary>
    public static void ShowMenu(Context context, View anchor, IReadOnlyList<SettingsMenuEntry> entries, Action<Exception> error)
    {
        var popup = new PopupMenu(context, anchor);
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            IMenuItem? item;
            if (entry.IsDestructive)
            {
                var title = new SpannableString(entry.Title);
                title.SetSpan(new ForegroundColorSpan(WinoColors.Critical(context)), 0, entry.Title.Length, SpanTypes.ExclusiveExclusive);
                item = popup.Menu.Add(0, index, index, title);
            }
            else item = popup.Menu.Add(0, index, index, entry.Title);
            if (entry.IsChecked) item?.SetCheckable(true)?.SetChecked(true);
        }
        popup.MenuItemClick += (_, args) =>
        {
            int index = args.Item?.ItemId ?? -1;
            if (index < 0 || index >= entries.Count) return;
            try { entries[index].Invoke(); }
            catch (Exception exception) { error(exception); }
        };
        popup.Show();
    }

    /// <summary>Windows GetFilterActionIconForeground: success, caution, critical or attention (accent).</summary>
    public static Color ActionTone(Context context, MailFilterActionType? action) => MailFilterDisplay.GetActionTone(action) switch
    {
        MailFilterActionTone.Success => WinoColors.Success(context),
        MailFilterActionTone.Caution => WinoColors.Caution(context),
        MailFilterActionTone.Critical => WinoColors.Critical(context),
        _ => WinoColors.Accent(context)
    };

    /// <summary>
    /// A rounded tile with a tinted wash and a glyph in the tone colour (Windows filter row tile). Sizes are dimension
    /// resources.
    /// </summary>
    public static (FrameLayout Tile, WinoIconView Icon) Tile(Context context, WinoIconGlyph glyph, Color tone,
        int size = Resource.Dimension.wino_size_swatch_large, int glyphSize = Resource.Dimension.wino_glyph_m)
    {
        var tile = new FrameLayout(context) { Background = Surface(context, WinoColors.WithAlpha(tone, 0.12), null, Resource.Dimension.wino_radius_medium) };
        var icon = new WinoIconView(context) { Colorful = false, Icon = glyph, GlyphSize = WinoTokens.Dp(context, glyphSize), Gravity = GravityFlags.Center };
        icon.SetTextColor(tone);
        tile.AddView(icon, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        tile.LayoutParameters = new LinearLayout.LayoutParams(WinoTokens.Px(context, size), WinoTokens.Px(context, size));
        tile.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
        return (tile, icon);
    }

    /// <summary>Re-tints a <see cref="Tile"/>.</summary>
    public static void SetTile(Context context, FrameLayout tile, WinoIconView icon, WinoIconGlyph glyph, Color tone)
    {
        tile.Background = Surface(context, WinoColors.WithAlpha(tone, 0.12), null, Resource.Dimension.wino_radius_medium);
        icon.Icon = glyph;
        icon.SetTextColor(tone);
    }

    /// <summary>A glyph view of a fixed size (<paramref name="size"/>: a wino_glyph_* dimension resource).</summary>
    public static WinoIconView Glyph(Context context, WinoIconGlyph glyph, int size, Color? tint = null, bool? colorful = null)
    {
        var icon = new WinoIconView(context) { Icon = glyph, GlyphSize = WinoTokens.Dp(context, size), Gravity = GravityFlags.Center };
        if (colorful is { } value) icon.Colorful = value;
        if (tint is { } color) icon.SetTextColor(color);
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        return icon;
    }

    /// <summary>Layout parameters for a child of a horizontal row: optional weight, start margin (a dimension resource, 0 for none).</summary>
    public static LinearLayout.LayoutParams RowItem(Context context, float weight = 0, int marginStart = 0, int width = ViewGroup.LayoutParams.WrapContent)
    {
        var layout = new LinearLayout.LayoutParams(weight > 0 ? 0 : width, ViewGroup.LayoutParams.WrapContent, weight);
        layout.MarginStart = marginStart == 0 ? 0 : WinoTokens.Px(context, marginStart);
        return layout;
    }

    /// <summary>A horizontal row, centred vertically.</summary>
    public static LinearLayout HRow(Context context)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        return row;
    }

    /// <summary>A vertical column.</summary>
    public static LinearLayout VColumn(Context context) => new(context) { Orientation = Orientation.Vertical };

    /// <summary>
    /// Adds <paramref name="view"/> to a vertical column, stretched, with a top gap (a dimension resource, 0 for none)
    /// after the first child.
    /// </summary>
    public static T AddStretched<T>(LinearLayout column, T view, int spacing = 0) where T : View
    {
        var context = column.RequireContext();
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        if (column.ChildCount > 0 && spacing != 0) layout.TopMargin = WinoTokens.Px(context, spacing);
        column.AddView(view, layout);
        return view;
    }

    /// <summary>Wraps buttons onto as many lines as they need (a button row at phone width).</summary>
    public static Google.Android.Material.Chip.ChipGroup Wrap(Context context, params View[] views)
    {
        var group = new Google.Android.Material.Chip.ChipGroup(context)
        {
            ChipSpacingHorizontal = WinoTokens.Px(context, Resource.Dimension.wino_space_s),
            ChipSpacingVertical = WinoTokens.Px(context, Resource.Dimension.wino_space_s)
        };
        foreach (var view in views) group.AddView(view, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return group;
    }

    /// <summary>Executes a command when it can execute; errors go to <paramref name="error"/>.</summary>
    public static void Execute(ICommand command, object? parameter, Action<Exception> error)
    {
        try
        {
            if (command.CanExecute(parameter)) command.Execute(parameter);
        }
        catch (Exception exception) { error(exception); }
    }

    /// <summary>Observes a fire-and-forget operation; errors go to <paramref name="error"/>.</summary>
    public static async void Run(Task operation, Action<Exception> error)
    {
        try { await operation; }
        catch (Exception exception) { error(exception); }
    }

    /// <summary>Icon-only buttons carry the action and the item name as their accessible name.</summary>
    public static string ActionLabel(string action, string name) => string.IsNullOrEmpty(name) ? action : $"{action}: {name}";
}

/// <summary>
/// The colour swatches (the iOS WinoColorSwatchPicker): 32 dp circles that wrap onto as many lines as they need, the
/// selected one ringed; a tap raises <see cref="SelectionChanged"/> with the colour's hex value.
/// </summary>
internal sealed class AccountColorSwatches
{
    private readonly Context _context;
    private readonly WinoFlowView _row;
    private IReadOnlyList<string> _colors = [];
    private string? _selected;

    public AccountColorSwatches(Context context, string? accessibilityLabel)
    {
        _context = context;
        _row = new WinoFlowView(context) { ContentDescription = accessibilityLabel };
        View = _row;
    }

    public View View { get; }

    public event EventHandler<string>? SelectionChanged;

    public IReadOnlyList<string> Colors
    {
        get => _colors;
        set { _colors = value; Render(); }
    }

    public string? SelectedHex
    {
        get => _selected;
        set { _selected = value; Render(); }
    }

    private void Render()
    {
        _row.RemoveAllViews();
        foreach (var hex in _colors)
        {
            var isSelected = string.Equals(hex, _selected, StringComparison.OrdinalIgnoreCase);
            var fill = WinoColors.FromHexString(hex) ?? WinoColors.Accent(_context);
            var swatch = new View(_context) { ContentDescription = hex, Clickable = true, Focusable = true, Selected = isSelected };
            var drawable = new GradientDrawable();
            drawable.SetShape(ShapeType.Oval);
            drawable.SetColor(fill);
            if (isSelected) drawable.SetStroke(WinoTokens.Px(_context, Resource.Dimension.wino_accounts_swatch_ring), WinoColors.PrimaryText(_context));
            swatch.Background = drawable;
            var captured = hex;
            swatch.Click += (_, _) => SelectionChanged?.Invoke(this, captured);
            var size = WinoTokens.Px(_context, Resource.Dimension.wino_size_swatch_large);
            var layout = new ViewGroup.MarginLayoutParams(size, size);
            int horizontal = WinoTokens.Px(_context, Resource.Dimension.wino_space_2xs), vertical = WinoTokens.Px(_context, Resource.Dimension.wino_space_s);
            layout.SetMargins(horizontal, vertical, horizontal, vertical);
            _row.AddView(swatch, layout);
        }
    }
}
