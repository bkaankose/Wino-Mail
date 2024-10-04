using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels
{
    public class SettingsPageViewModel : CoreBaseViewModel
    {
        public SettingsPageViewModel(IDialogService dialogService, INavigationService navigationService) : base(dialogService)
        {
            NavigationService = navigationService;
        }

        public INavigationService NavigationService { get; }
    }
}
