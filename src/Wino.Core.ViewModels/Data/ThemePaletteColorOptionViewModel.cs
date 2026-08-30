using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

public partial class ThemePaletteColorOptionViewModel : ObservableObject
{
    public ThemePaletteColorOptionViewModel(CustomThemeColorKey key, string label, string group, string value, bool isOverridden)
    {
        Key = key;
        Label = label;
        Group = group;
        Value = value;
        IsOverridden = isOverridden;
    }

    public CustomThemeColorKey Key { get; }
    public string Label { get; }
    public string Group { get; }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial bool IsOverridden { get; set; }
}
