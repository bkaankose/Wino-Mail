using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels
{
    public class AppShellViewModel : CoreBaseViewModel
    {
        public AppShellViewModel(IDialogService dialogService) : base(dialogService)
        {
        }
    }
}
