using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels
{
    public partial class IdlePageViewModel : BaseViewModel
    {
        public IdlePageViewModel(IDialogService dialogService) : base(dialogService) { }
    }
}
