using System.Globalization;

namespace Wino.Mail.Controls.Core.Settings;

/// <summary>
/// Value rules of the native settings binders (AppKit and UIKit SettingsBinder): which list index a bound
/// value selects, how a typed stepper value is read back, and the minute precision of time pickers.
/// </summary>
public static class SettingsValueRules
{
    /// <summary>The index to select for a bound value: the value inside 0..count-1, otherwise -1 (no selection).</summary>
    public static int SelectableIndex(int value, int count) => value >= 0 && value < count ? value : -1;

    /// <summary>A stepper value clamped to its bounds.</summary>
    public static int ClampStepper(int value, int minimum, int maximum) => Math.Clamp(value, minimum, Math.Max(minimum, maximum));

    /// <summary>
    /// The value a stepper text field commits: the parsed integer clamped to its bounds, or <paramref name="fallback"/>
    /// (the current source value, clamped) when the text is not a number.
    /// </summary>
    public static int ParseStepper(string? text, int fallback, int minimum, int maximum)
    {
        var value = int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed) ? parsed : fallback;
        return ClampStepper(value, minimum, maximum);
    }

    /// <summary>The hour and minute of a picked time of day; seconds and fractions are dropped (Windows TimePicker).</summary>
    public static TimeSpan TimeOfDay(TimeSpan picked) => new(picked.Hours, picked.Minutes, 0);
}
