using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>Small pieces shared by the account sub-pages' custom rows: tags, glyph toggles and accent pill buttons (Mac SettingsRowParts).</summary>
internal static class SettingsRowParts
{
    /// <summary>Raised row surface inside an expander (white in light, a faint lift in dark).</summary>
    public static UIColor RowFill => WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0xFFFFFF, 0.06));

    /// <summary>Accent wash behind pill buttons and inline hints.</summary>
    public static UIColor AccentWash => WinoStyle.Accent.ColorWithAlpha(0.1f);

    /// <summary>A rounded 20pt caption tag ("System", "Hidden").</summary>
    public static UIView Tag(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        var tag = new WinoSurfaceView { Fill = WinoSettingsStyle.SelectedFill, CornerRadius = 10, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, tag, 2, 8, 2, 8);
        tag.HeightAnchor.ConstraintGreaterThanOrEqualTo(20).Active = true;
        WinoLayout.Priorities(tag, UILayoutConstraintAxis.Horizontal, 750, 750);
        return tag;
    }

    /// <summary>Fixed-size empty slot that keeps row columns aligned where a button is absent.</summary>
    public static UIView Slot(double width = 44, double height = 44)
        => WinoLayout.Size(new UIView { TranslatesAutoresizingMaskIntoConstraints = false }, width, height);
}

/// <summary>
/// 44×44 touch target with a 17pt glyph and an optional pressed (toggled) wash: the Windows subtle ToggleButton/Button in
/// the folder rows. The accessibility label is required. Built with alloc/init (not <c>buttonWithType:</c>, which
/// returns a plain UIButton whose managed subclass peer, and with it the tap handlers, is lost to the GC).
/// </summary>
internal sealed class SettingsGlyphButton : UIButton
{
    private readonly WinoIconGlyph _glyph;
    private bool _isToggled;

    public SettingsGlyphButton(WinoIconGlyph glyph, string label)
    {
        _glyph = glyph;
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Size(this, 44, 44);
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        Configuration = configuration;
        Label = label;
        Apply();
        WinoTraits.OnAppearanceChanged(this, static view => view.Apply());
    }

    public string Label
    {
        set => WinoAccessibility.Label(this, value);
    }

    /// <summary>Pressed state: wash behind the glyph and primary tint (Windows ToggleButton IsChecked).</summary>
    public bool IsToggled
    {
        get => _isToggled;
        set
        {
            _isToggled = value;
            AccessibilityValue = value ? "1" : "0";
            Apply();
        }
    }

    private void Apply()
    {
        if (Configuration is not { } configuration) return;
        var tint = _isToggled ? WinoStyle.PrimaryText : WinoStyle.SecondaryText;
        configuration.Image = WinoIcons.Image(_glyph, 17, tint);
        configuration.BaseForegroundColor = tint;
        // Configurations and their backgrounds are copies: change the copy, then assign it back.
        var background = configuration.Background;
        background.BackgroundColor = _isToggled ? WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.09), WinoStyle.Hex(0xFFFFFF, 0.12)) : UIColor.Clear;
        background.CornerRadius = 8;
        configuration.Background = background;
        Configuration = configuration;
        TintColor = tint;
    }
}

/// <summary>Small accent-text button on an accent wash ("Link", "Unlink", "Choose folders to count"); alloc/init, see <see cref="SettingsGlyphButton"/>.</summary>
internal sealed class SettingsPillButton : UIButton
{
    public SettingsPillButton(string title, WinoIconGlyph icon = WinoIconGlyph.None, bool washed = true)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Washed = washed;
        var configuration = washed ? UIButtonConfiguration.TintedButtonConfiguration : UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title ?? string.Empty;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        if (icon != WinoIconGlyph.None)
        {
            configuration.Image = WinoIcons.Image(icon, 13, WinoStyle.Accent);
            configuration.ImagePadding = 4;
        }
        Configuration = configuration;
        HeightAnchor.ConstraintGreaterThanOrEqualTo(32).Active = true;
        WinoLayout.Priorities(this, UILayoutConstraintAxis.Horizontal, 750, 750);
        WinoAccessibility.Label(this, title);
    }

    public bool Washed { get; }
}
