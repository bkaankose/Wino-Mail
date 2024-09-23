using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels
{
    public class SettingsDialogViewModel : CoreBaseViewModel
    {
        public SettingsDialogViewModel(IDialogService dialogService) : base(dialogService)
        {
        }
    }
}
