using CommunityToolkit.Mvvm.ComponentModel;

namespace Wino.Mail.ViewModels.Data
{
    public class AppColorViewModel : ObservableObject
    {
        private string _hex;

        public string Hex
        {
            get => _hex;
            set => SetProperty(ref _hex, value);
        }

        public bool IsAccentColor { get; }

        public AppColorViewModel(string hex, bool isAccentColor = false)
        {
            IsAccentColor = isAccentColor;
            Hex = hex;
        }
    }
}
