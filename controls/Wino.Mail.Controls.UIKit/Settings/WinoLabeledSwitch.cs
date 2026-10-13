using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// The Windows ToggleSwitch with its On/Off content (UIKit twin of the AppKit <c>WinoLabeledSwitch</c>): a secondary
/// label followed by a <see cref="UISwitch"/>. The label keeps the width of the longer word so the switch never moves
/// when it toggles. Phone pages usually hide the label (<see cref="ShowsLabel"/>), as iOS switches carry no text.
/// </summary>
public sealed class WinoLabeledSwitch : UIView
{
    private readonly UILabel _label;

    public WinoLabeledSwitch()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Switch = new UISwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        _label = WinoStyle.Label(Translator.CalendarSettings_Toggle_Off, WinoStyle.Body, WinoStyle.SecondaryText);
        _label.TextAlignment = UITextAlignment.Right;
        var font = WinoStyle.Body;
        var width = Math.Max(Measure(Translator.CalendarSettings_Toggle_On, font), Measure(Translator.CalendarSettings_Toggle_Off, font));
        _label.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)Math.Ceiling(width)).Active = true;
        _label.IsAccessibilityElement = false;
        var row = WinoLayout.HStack(8, _label, Switch);
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

    private void UpdateLabel() => _label.Text = IsOn ? Translator.CalendarSettings_Toggle_On : Translator.CalendarSettings_Toggle_Off;

    private static double Measure(string text, UIFont font)
        => new NSAttributedString(text ?? string.Empty, new UIStringAttributes { Font = font }).Size.Width;
}
