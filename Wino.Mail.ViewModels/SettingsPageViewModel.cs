using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels
{
    public class SettingsPageViewModel : BaseViewModel
    {
        public SettingsPageViewModel(IDialogService dialogService) : base(dialogService)
        {
        }
    }
}
