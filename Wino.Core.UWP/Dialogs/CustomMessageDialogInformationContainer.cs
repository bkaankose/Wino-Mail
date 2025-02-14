using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Dialogs
{
    public partial class CustomMessageDialogInformationContainer : ObservableObject
    {
        [ObservableProperty]
        public partial bool IsDontAskChecked { get; set; }

        public CustomMessageDialogInformationContainer(string title, string description, WinoCustomMessageDialogIcon icon, bool isDontAskAgainEnabled)
        {
            Title = title;
            Description = description;
            Icon = icon;
            IsDontAskAgainEnabled = isDontAskAgainEnabled;
        }

        public string Title { get; }
        public string Description { get; }
        public WinoCustomMessageDialogIcon Icon { get; }
        public bool IsDontAskAgainEnabled { get; }
    }
}
