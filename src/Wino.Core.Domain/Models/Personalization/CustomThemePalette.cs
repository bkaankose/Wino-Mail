using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

/// <summary>
/// The supported custom-theme surface colors. Null values inherit from the base surface
/// or the mode default; arbitrary resource keys are intentionally not supported.
/// </summary>
public sealed class CustomThemePalette
{
    public string? MainCustomThemeColor { get; set; }
    public string? MailListHeaderBackgroundColor { get; set; }
    public string? CalendarDefaultHourBackgroundBrush { get; set; }
    public string? CalendarHoverHourBackgroundBrush { get; set; }
    public string? CalendarWorkHourBackgroundBrush { get; set; }
    public string? CalendarSelectedHourBackgroundBrush { get; set; }
    public string? WinoContentZoneBackgroud { get; set; }
    public string? ReadingPaneBackgroundColorBrush { get; set; }
    public string? NavigationViewContentBackground { get; set; }

    public CustomThemePalette Clone() => (CustomThemePalette)MemberwiseClone();

    public string? GetOverride(CustomThemeColorKey key) => key switch
    {
        CustomThemeColorKey.BaseSurface => MainCustomThemeColor,
        CustomThemeColorKey.MailListHeader => MailListHeaderBackgroundColor,
        CustomThemeColorKey.Workspace => WinoContentZoneBackgroud,
        CustomThemeColorKey.Navigation => NavigationViewContentBackground,
        CustomThemeColorKey.ReadingPane => ReadingPaneBackgroundColorBrush,
        CustomThemeColorKey.CalendarDefaultHour => CalendarDefaultHourBackgroundBrush,
        CustomThemeColorKey.CalendarHoverHour => CalendarHoverHourBackgroundBrush,
        CustomThemeColorKey.CalendarWorkHour => CalendarWorkHourBackgroundBrush,
        CustomThemeColorKey.CalendarSelectedHour => CalendarSelectedHourBackgroundBrush,
        _ => null
    };

    public void SetOverride(CustomThemeColorKey key, string? value)
    {
        switch (key)
        {
            case CustomThemeColorKey.BaseSurface: MainCustomThemeColor = value; break;
            case CustomThemeColorKey.MailListHeader: MailListHeaderBackgroundColor = value; break;
            case CustomThemeColorKey.Workspace: WinoContentZoneBackgroud = value; break;
            case CustomThemeColorKey.Navigation: NavigationViewContentBackground = value; break;
            case CustomThemeColorKey.ReadingPane: ReadingPaneBackgroundColorBrush = value; break;
            case CustomThemeColorKey.CalendarDefaultHour: CalendarDefaultHourBackgroundBrush = value; break;
            case CustomThemeColorKey.CalendarHoverHour: CalendarHoverHourBackgroundBrush = value; break;
            case CustomThemeColorKey.CalendarWorkHour: CalendarWorkHourBackgroundBrush = value; break;
            case CustomThemeColorKey.CalendarSelectedHour: CalendarSelectedHourBackgroundBrush = value; break;
        }
    }

    public void ResetOverride(CustomThemeColorKey key) => SetOverride(key, null);

    public CustomThemePalette Resolve(bool isDark)
    {
        var modeDefaults = CreateDefaults(isDark);
        var baseColor = Normalize(MainCustomThemeColor) ?? modeDefaults.MainCustomThemeColor!;

        return new CustomThemePalette
        {
            MainCustomThemeColor = baseColor,
            MailListHeaderBackgroundColor = Normalize(MailListHeaderBackgroundColor) ?? modeDefaults.MailListHeaderBackgroundColor,
            CalendarDefaultHourBackgroundBrush = Normalize(CalendarDefaultHourBackgroundBrush) ?? WithOpacity(baseColor, 0.55),
            CalendarHoverHourBackgroundBrush = Normalize(CalendarHoverHourBackgroundBrush) ?? WithOpacity(baseColor, 0.70),
            CalendarWorkHourBackgroundBrush = Normalize(CalendarWorkHourBackgroundBrush) ?? WithOpacity(baseColor, 0.85),
            CalendarSelectedHourBackgroundBrush = Normalize(CalendarSelectedHourBackgroundBrush) ?? modeDefaults.CalendarSelectedHourBackgroundBrush,
            WinoContentZoneBackgroud = Normalize(WinoContentZoneBackgroud) ?? baseColor,
            ReadingPaneBackgroundColorBrush = Normalize(ReadingPaneBackgroundColorBrush) ?? baseColor,
            NavigationViewContentBackground = Normalize(NavigationViewContentBackground) ?? "#00000000"
        };
    }

    public static CustomThemePalette CreateDefaults(bool isDark)
    {
        var result = new CustomThemePalette
        {
            MainCustomThemeColor = isDark ? "#E61F1F1F" : "#D9FFFFFF",
            MailListHeaderBackgroundColor = isDark ? "#FF1F1F1F" : "#FFECF0F1",
            CalendarSelectedHourBackgroundBrush = isDark ? "#66399BFF" : "#4D0078D4",
            NavigationViewContentBackground = "#00000000"
        };
        var baseColor = result.MainCustomThemeColor;
        result.CalendarDefaultHourBackgroundBrush = WithOpacity(baseColor, 0.55);
        result.CalendarHoverHourBackgroundBrush = WithOpacity(baseColor, 0.70);
        result.CalendarWorkHourBackgroundBrush = WithOpacity(baseColor, 0.85);
        result.WinoContentZoneBackgroud = baseColor;
        result.ReadingPaneBackgroundColorBrush = baseColor;
        return result;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string WithOpacity(string color, double opacity)
    {
        var value = color.TrimStart('#');
        var rgb = value.Length == 8 ? value[2..] : value;
        var alpha = (byte)Math.Round(255 * opacity);
        return $"#{alpha:X2}{rgb.ToUpperInvariant()}";
    }
}
