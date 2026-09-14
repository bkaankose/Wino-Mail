using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// One editable color of a custom theme palette, together with the miniature surface that
/// previews it and the contrast of body text placed on it.
/// </summary>
public partial class ThemePaletteColorOptionViewModel : ObservableObject
{
    public ThemePaletteColorOptionViewModel(CustomThemeColorKey key, string value, bool isOverridden)
    {
        Key = key;
        Label = CustomThemeColorCatalog.GetLabel(key);
        Description = CustomThemeColorCatalog.GetDescription(key);
        Scene = CustomThemeColorCatalog.GetScene(key);
        Group = CustomThemeColorCatalog.GetGroup(key);
        CarriesBodyText = CustomThemeColorCatalog.CarriesBodyText(key);
        Value = value;
        IsOverridden = isOverridden;
    }

    public CustomThemeColorKey Key { get; }
    public string Label { get; }
    public string Description { get; }
    public ThemeSurfaceScene Scene { get; }
    public CustomThemeColorGroup Group { get; }

    /// <summary>
    /// True when readable body text sits on this surface, so its contrast is reported.
    /// </summary>
    public bool CarriesBodyText { get; }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial bool IsOverridden { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool HasContrastReport { get; set; }

    [ObservableProperty]
    public partial bool IsContrastSufficient { get; set; }

    [ObservableProperty]
    public partial string ContrastMessage { get; set; } = string.Empty;

    public void ReportContrast(double? ratio)
    {
        if (ratio is not double value)
        {
            HasContrastReport = false;
            ContrastMessage = string.Empty;
            return;
        }

        var sufficient = ThemeContrastCalculator.IsSufficient(value);
        var format = sufficient
            ? Translator.ApplicationThemeEditor_ContrastPassFormat
            : Translator.ApplicationThemeEditor_ContrastFailFormat;

        IsContrastSufficient = sufficient;
        ContrastMessage = string.Format(format, value.ToString("0.0"));
        HasContrastReport = true;
    }
}
