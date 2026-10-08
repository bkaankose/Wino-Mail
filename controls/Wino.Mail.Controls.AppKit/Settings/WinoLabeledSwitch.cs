using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// The Windows ToggleSwitch with its On/Off content: a secondary label followed by an NSSwitch.
/// The label keeps the width of the longer word so the switch never moves when it toggles.
/// </summary>
public sealed class WinoLabeledSwitch : NSView
{
    private readonly NSTextField _label;

    public WinoLabeledSwitch()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Switch = new NSSwitch { TranslatesAutoresizingMaskIntoConstraints = false, ControlSize = NSControlSize.Regular };
        _label = WinoStyle.Label(Translator.CalendarSettings_Toggle_Off, WinoStyle.Body, WinoStyle.SecondaryText);
        _label.Alignment = NSTextAlignment.Right;
        var width = Math.Max(Measure(Translator.CalendarSettings_Toggle_On), Measure(Translator.CalendarSettings_Toggle_Off));
        _label.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)Math.Ceiling(width)).Active = true;
        var row = WinoLayout.HStack(8, _label, Switch);
        WinoLayout.Fill(row, this);
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        Switch.Activated += (_, _) => { UpdateLabel(); Toggled?.Invoke(this, EventArgs.Empty); };
        UpdateLabel();
    }

    public NSSwitch Switch { get; }

    /// <summary>Raised after the user flips the switch (not when <see cref="IsOn"/> is set in code).</summary>
    public event EventHandler? Toggled;

    public bool IsOn
    {
        get => Switch.State != 0;
        set { Switch.State = value ? 1 : 0; UpdateLabel(); }
    }

    public bool ShowsLabel
    {
        get => !_label.Hidden;
        set => _label.Hidden = !value;
    }

    public bool IsEnabled
    {
        get => Switch.Enabled;
        set { Switch.Enabled = value; _label.AlphaValue = value ? 1 : 0.55f; }
    }

    private void UpdateLabel() => _label.StringValue = IsOn ? Translator.CalendarSettings_Toggle_On : Translator.CalendarSettings_Toggle_Off;

    private static double Measure(string text)
        => new NSAttributedString(text ?? string.Empty, new NSStringAttributes { Font = WinoStyle.Body }).Size.Width;
}
