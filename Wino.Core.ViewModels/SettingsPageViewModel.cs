using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels
{
    public class SettingsPageViewModel : CoreBaseViewModel
    {
        public SettingsPageViewModel(INavigationService navigationService)
        {
            NavigationService = navigationService;
        }

        public INavigationService NavigationService { get; }
    }
}
