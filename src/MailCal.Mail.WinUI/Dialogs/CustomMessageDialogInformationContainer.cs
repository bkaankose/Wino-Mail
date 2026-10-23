using CommunityToolkit.Mvvm.ComponentModel;
using MailCal.Core.Domain.Enums;

namespace MailCal.Dialogs;

public partial class CustomMessageDialogInformationContainer : ObservableObject
{
    [ObservableProperty]
    public partial bool IsDontAskChecked { get; set; }

    public CustomMessageDialogInformationContainer(string title, string description, MailCalCustomMessageDialogIcon icon, bool isDontAskAgainEnabled)
    {
        Title = title;
        Description = description;
        Icon = icon;
        IsDontAskAgainEnabled = isDontAskAgainEnabled;
    }

    public string Title { get; }
    public string Description { get; }
    public MailCalCustomMessageDialogIcon Icon { get; }
    public bool IsDontAskAgainEnabled { get; }
}
