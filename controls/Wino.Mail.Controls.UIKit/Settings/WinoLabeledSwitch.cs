using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// The Windows ToggleSwitch with its On/Off content (UIKit twin of the AppKit <c>WinoLabeledSwitch</c>): an optional
/// secondary label followed by a native <see cref="UISwitch"/>. iOS switches carry no text, so the label is hidden unless
/// a page turns <see cref="ShowsLabel"/> on; it keeps the width of the longer word so the switch never moves when it
/// toggles. The switch keeps its native 51 × 31 size (design decision 15 Oct 2026: a scaled UISwitch blurs the knob,
/// breaks VoiceOver's frame and, with a widened hit area, lost its touches); settings rows are 52pt tall so it has room.
/// </summary>
public sealed class WinoLabeledSwitch : UIView
{
    private readonly UILabel _label;
    private readonly string _onText;
    private readonly string _offText;

    public WinoLabeledSwitch() : this(Translator.CalendarSettings_Toggle_On, Translator.CalendarSettings_Toggle_Off)
    {
    }

    /// <summary>A switch whose optional label reads <paramref name="onText"/> and <paramref name="offText"/> (Allowed / Not allowed).</summary>
    public WinoLabeledSwitch(string onText, string offText)
    {
        _onText = onText ?? string.Empty;
        _offText = offText ?? string.Empty;
        TranslatesAutoresizingMaskIntoConstraints = false;
        Switch = new UISwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        _label = WinoStyle.Label(_offText, WinoStyle.Body, WinoStyle.SecondaryText);
        _label.TextAlignment = UITextAlignment.Right;
        var font = WinoStyle.Body;
        var width = Math.Max(Measure(_onText, font), Measure(_offText, font));
        _label.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)Math.Ceiling(width)).Active = true;
        _label.IsAccessibilityElement = false;
        _label.Hidden = true;

        var row = WinoLayout.HStack(8, _label, Switch);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(row, this);
        WinoLayout.Priorities(this, UILayoutConstraintAxis.Horizontal, 750, 750);
        Switch.ValueChanged += (_, _) => { UpdateLabel(); Toggled?.Invoke(this, EventArgs.Empty); };
        UpdateLabel();
    }

    public UISwitch Switch { get; }

    /// <summary>Raised after the user flips the switch (not when <see cref="IsOn"/> is set in code).</summary>
    public event EventHandler? Toggled;

    public bool IsOn
    {
        get => Switch.On;
        set { Switch.On = value; UpdateLabel(); }
    }

    /// <summary>Shows the Windows On/Off text before the switch. Off by default on iOS.</summary>
    public bool ShowsLabel
    {
        get => !_label.Hidden;
        set => _label.Hidden = !value;
    }

    public bool IsEnabled
    {
        get => Switch.Enabled;
        set { Switch.Enabled = value; _label.Alpha = value ? 1 : 0.55f; }
    }

    private void UpdateLabel()
    {
        _label.Text = IsOn ? _onText : _offText;
        Switch.AccessibilityValue = _label.Text;
    }

    private static double Measure(string text, UIFont font)
        => new NSAttributedString(text ?? string.Empty, new UIStringAttributes { Font = font }).Size.Width;
}
