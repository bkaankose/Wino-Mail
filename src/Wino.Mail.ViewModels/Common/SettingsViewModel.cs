using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

public partial class SettingsDialogViewModel : CoreBaseViewModel
{
    public SettingsDialogViewModel(IMessenger messenger, IMailDialogService dialogService) : base(messenger)
    {
    }
}
