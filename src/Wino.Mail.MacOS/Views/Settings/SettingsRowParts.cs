using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Small pieces shared by the account sub-pages' custom rows: tags, icon toggles and accent pill buttons.</summary>
internal static class SettingsRowParts
{
    /// <summary>Raised row surface inside an expander (white in light, a faint lift in dark).</summary>
    public static NSColor RowFill => WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0xFFFFFF, 0.06));

    /// <summary>Accent wash behind pill buttons and inline hints.</summary>
    public static NSColor AccentWash => WinoStyle.Accent.ColorWithAlphaComponent(0.1f);

    /// <summary>A rounded 18pt caption tag ("System", "Hidden").</summary>
    public static NSView Tag(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        var tag = new WinoSurfaceView { Fill = WinoSettingsStyle.SelectedFill, CornerRadius = 9, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, tag, 2, 7, 2, 7);
        tag.HeightAnchor.ConstraintEqualTo(18).Active = true;
        tag.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        tag.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        return tag;
    }

    /// <summary>Fixed-size empty slot that keeps row columns aligned where a button is absent.</summary>
    public static NSView Slot(double width = 28, double height = 26)
        => WinoLayout.Size(new NSView { TranslatesAutoresizingMaskIntoConstraints = false }, width, height);
}

/// <summary>
/// 28×26 borderless glyph button with an optional pressed (toggled) wash, the Windows subtle
/// ToggleButton/Button in the folder rows. Tooltip and accessibility label are required.
/// </summary>
internal sealed class SettingsGlyphButton : NSButton
{
    private bool _isToggled;
    private readonly WinoIconGlyph _glyph;

    public SettingsGlyphButton(WinoIconGlyph glyph, string label)
    {
        _glyph = glyph;
        TranslatesAutoresizingMaskIntoConstraints = false;
        Bordered = false;
        Title = string.Empty;
        ImagePosition = NSCellImagePosition.ImageOnly;
        WantsLayer = true;
        Layer!.CornerRadius = 5;
        WinoLayout.Size(this, 28, 26);
        Label = label;
        Apply();
    }

    public string Label
    {
        set
        {
            ToolTip = value;
            WinoAccessibility.Label(this, value);
        }
    }

    /// <summary>Pressed state: wash behind the glyph and primary tint (Windows ToggleButton IsChecked).</summary>
    public bool IsToggled
    {
        get => _isToggled;
        set { _isToggled = value; Apply(); }
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Apply();
    }

    private void Apply()
    {
        Image = WinoIcons.Image(_glyph, 15, _isToggled ? WinoStyle.PrimaryText : WinoStyle.SecondaryText);
        ContentTintColor = _isToggled ? WinoStyle.PrimaryText : WinoStyle.SecondaryText;
        CGColor? fill = null;
        EffectiveAppearance.PerformAsCurrentDrawingAppearance(() =>
            fill = _isToggled ? WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.09), WinoStyle.Hex(0xFFFFFF, 0.12)).CGColor : null);
        Layer!.BackgroundColor = fill;
    }
}

/// <summary>Small accent-text button on an accent wash ("Link", "Unlink", "Choose folders to count").</summary>
internal sealed class SettingsPillButton : NSButton
{
    public SettingsPillButton(string title, WinoIconGlyph icon = WinoIconGlyph.None, bool washed = true)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Bordered = false;
        var font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold);
        AttributedTitle = new Foundation.NSAttributedString(title ?? string.Empty, new NSStringAttributes { Font = font, ForegroundColor = WinoStyle.Accent });
        if (icon != WinoIconGlyph.None)
        {
            Image = WinoIcons.Image(icon, 12, WinoStyle.Accent);
            ImagePosition = NSCellImagePosition.ImageLeading;
            ImageHugsTitle = true;
        }
        WantsLayer = true;
        Layer!.CornerRadius = 5;
        Washed = washed;
        HeightAnchor.ConstraintEqualTo(24).Active = true;
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        WinoAccessibility.Label(this, title);
        Apply();
    }

    public bool Washed { get; }

    public override CGSize IntrinsicContentSize
    {
        get
        {
            var size = base.IntrinsicContentSize;
            return new CGSize(size.Width + (Washed ? 20 : 4), 24);
        }
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        Apply();
    }

    private void Apply()
    {
        if (!Washed) return;
        CGColor? fill = null;
        EffectiveAppearance.PerformAsCurrentDrawingAppearance(() => fill = SettingsRowParts.AccentWash.CGColor);
        Layer!.BackgroundColor = fill;
    }
}
