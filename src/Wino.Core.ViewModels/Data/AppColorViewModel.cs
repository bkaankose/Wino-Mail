using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;

namespace Wino.Core.ViewModels.Data;

public class AppColorViewModel : ObservableObject
{
    private string _hex;

    public string Hex
    {
        get => _hex;
        set
        {
            if (SetProperty(ref _hex, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName => string.Format(Translator.Accessibility_ColorOption, Hex);

    public bool IsAccentColor { get; }

    public AppColorViewModel(string hex, bool isAccentColor = false)
    {
        IsAccentColor = isAccentColor;
        Hex = hex;
    }

    public override string ToString() => DisplayName;
}
