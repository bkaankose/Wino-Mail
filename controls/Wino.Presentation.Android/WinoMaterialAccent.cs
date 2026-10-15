using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.Slider;
using Google.Android.Material.TextField;

namespace Wino.Presentation.Android;

/// <summary>
/// Paints Material Components in the selected theme's accent. The app theme's colorPrimary is the static Wino accent
/// resource (no Material dynamic colour), so controls created while a theme with another accent is active take
/// <see cref="WinoColors.Accent"/> through their tint lists. Activities are recreated when the accent changes, so a
/// control is tinted once when it is created.
/// </summary>
public static class WinoMaterialAccent
{
    private static readonly int[] Checked = [global::Android.Resource.Attribute.StateChecked];
    private static readonly int[] Disabled = [-global::Android.Resource.Attribute.StateEnabled];
    private static readonly int[] Focused = [global::Android.Resource.Attribute.StateFocused];
    private static readonly int[] Any = [];

    /// <summary>True when a theme accent differs from the resource accent.</summary>
    public static bool IsActive => WinoColors.AccentOverride is not null;

    public static MaterialSwitch Tint(MaterialSwitch control)
    {
        if (!IsActive || control.Context is not { } context) return control;
        var accent = WinoColors.Accent(context);
        control.TrackTintList = new ColorStateList([Checked, Any], [accent, WinoColors.FromResource(context, Resource.Color.wino_surface_variant)]);
        control.ThumbTintList = new ColorStateList([Checked, Any], [WinoColors.OnAccent(context), WinoColors.SecondaryText(context)]);
        return control;
    }

    public static MaterialButton Tint(MaterialButton button, bool filled)
    {
        if (!IsActive || button.Context is not { } context) return button;
        if (filled)
        {
            button.BackgroundTintList = ColorStateList.ValueOf(WinoColors.Accent(context));
            button.SetTextColor(WinoColors.OnAccent(context));
            button.IconTint = ColorStateList.ValueOf(WinoColors.OnAccent(context));
        }
        else
        {
            button.SetTextColor(WinoColors.AccentText(context));
        }
        return button;
    }

    public static CompoundButton Tint(CompoundButton button)
    {
        if (!IsActive || button.Context is not { } context) return button;
        button.ButtonTintList = new ColorStateList([Checked, Any], [WinoColors.Accent(context), WinoColors.SecondaryText(context)]);
        return button;
    }

    public static Slider Tint(Slider slider)
    {
        if (!IsActive || slider.Context is not { } context) return slider;
        var accent = ColorStateList.ValueOf(WinoColors.Accent(context));
        slider.TrackActiveTintList = accent;
        slider.ThumbTintList = accent;
        slider.HaloTintList = ColorStateList.ValueOf(WinoColors.WithAlpha(WinoColors.Accent(context), 0.2));
        return slider;
    }

    public static TextInputLayout Tint(TextInputLayout layout)
    {
        if (!IsActive || layout.Context is not { } context) return layout;
        var accent = WinoColors.Accent(context);
        layout.SetBoxStrokeColorStateList(new ColorStateList([Focused, Disabled, Any], [accent, WinoColors.Outline(context), WinoColors.Outline(context)]));
        layout.HintTextColor = ColorStateList.ValueOf(accent);
        return layout;
    }

    /// <summary>A checked toggle-group button: the accent wash behind accent text (canvas Components, segmented control).</summary>
    public static MaterialButton TintSegment(MaterialButton button)
    {
        if (button.Context is not { } context) return button;
        var accent = WinoColors.Accent(context);
        button.BackgroundTintList = new ColorStateList([Checked, Any], [WinoColors.WithAlpha(accent, 0.16), Color.Transparent]);
        button.SetTextColor(new ColorStateList([Checked, Any], [WinoColors.AccentText(context), WinoColors.PrimaryText(context)]));
        button.StrokeColor = ColorStateList.ValueOf(WinoColors.Outline(context));
        return button;
    }

    /// <summary>
    /// The mail list's Material 3 segmented button (canvas Material pass, Focused / Other): the secondary container
    /// behind the checked segment with its check icon, outline strokes, on-surface text.
    /// </summary>
    public static MaterialButton TintM3Segment(MaterialButton button)
    {
        if (button.Context is not { } context) return button;
        button.BackgroundTintList = new ColorStateList([Checked, Any], [WinoColors.SecondaryContainer(context), Color.Transparent]);
        var text = new ColorStateList([Checked, Any], [WinoColors.PrimaryText(context), WinoColors.SecondaryText(context)]);
        button.SetTextColor(text);
        button.IconTint = text;
        button.StrokeColor = ColorStateList.ValueOf(WinoColors.Outline(context));
        return button;
    }

    /// <summary>The navigation bar and rail: the pill indicator in the secondary container.</summary>
    public static Google.Android.Material.Navigation.NavigationBarView Tint(Google.Android.Material.Navigation.NavigationBarView bar)
    {
        if (bar.Context is not { } context) return bar;
        bar.ItemActiveIndicatorColor = ColorStateList.ValueOf(WinoColors.SecondaryContainer(context));
        var selected = new ColorStateList([Checked, Any], [WinoColors.PrimaryText(context), WinoColors.SecondaryText(context)]);
        bar.ItemIconTintList = selected;
        bar.ItemTextColor = selected;
        return bar;
    }

    /// <summary>The tonal FAB (primary container, 16 dp corners from the theme style).</summary>
    public static Google.Android.Material.FloatingActionButton.FloatingActionButton Tint(Google.Android.Material.FloatingActionButton.FloatingActionButton fab)
    {
        if (fab.Context is not { } context) return fab;
        fab.BackgroundTintList = ColorStateList.ValueOf(WinoColors.PrimaryContainer(context));
        fab.ImageTintList = ColorStateList.ValueOf(WinoColors.OnPrimaryContainer(context));
        return fab;
    }

    /// <summary>The fragment manager of the activity that hosts <paramref name="context"/> (for Material pickers).</summary>
    public static AndroidX.Fragment.App.FragmentManager? FragmentManager(Context? context)
    {
        while (context is not null)
        {
            if (context is AndroidX.Fragment.App.FragmentActivity activity) return activity.SupportFragmentManager;
            context = (context as ContextWrapper)?.BaseContext;
        }
        return null;
    }
}
