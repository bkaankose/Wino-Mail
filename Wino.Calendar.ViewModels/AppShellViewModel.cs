using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels
{
    public partial class AppShellViewModel : CalendarBaseViewModel
    {
        public IPreferencesService PreferencesService { get; }
        public IStatePersistanceService StatePersistanceService { get; }
        public INavigationService NavigationService { get; }

        public AppShellViewModel(IPreferencesService preferencesService, IStatePersistanceService statePersistanceService, INavigationService navigationService)
        {
            PreferencesService = preferencesService;
            StatePersistanceService = statePersistanceService;
            NavigationService = navigationService;
        }

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            NavigationService.Navigate(WinoPage.CalendarPage, null, NavigationReferenceFrame.ShellFrame, NavigationTransitionType.DrillIn);
        }
    }
}
