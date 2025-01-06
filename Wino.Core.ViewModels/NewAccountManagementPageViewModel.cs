using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels
{
    public class ManageAccountsPagePageViewModel : CoreBaseViewModel
    {
        public ManageAccountsPagePageViewModel(INavigationService navigationService)
        {
            NavigationService = navigationService;
        }

        public INavigationService NavigationService { get; }
    }
}
