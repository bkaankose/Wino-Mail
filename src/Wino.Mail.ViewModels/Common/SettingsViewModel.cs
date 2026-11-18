using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Common;

public partial class SettingsDialogViewModel : CoreBaseViewModel
{
    public SettingsDialogViewModel(IMessenger messenger, IMailDialogService dialogService) : base(messenger)
    {
    }
}
