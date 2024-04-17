using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels
{
    public class SettingsDialogViewModel : BaseViewModel
    {
        public SettingsDialogViewModel(IDialogService dialogService) : base(dialogService)
        {
        }
    }
}
