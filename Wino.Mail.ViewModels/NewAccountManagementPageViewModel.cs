using Wino.Domain.Interfaces;

namespace Wino.Mail.ViewModels
{
    public class NewAccountManagementPageViewModel : BaseViewModel
    {
        public NewAccountManagementPageViewModel(IDialogService dialogService) : base(dialogService)
        {
        }
    }
}
