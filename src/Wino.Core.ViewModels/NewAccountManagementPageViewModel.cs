using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

public class ManageAccountsPagePageViewModel : CoreBaseViewModel
{
    public ManageAccountsPagePageViewModel(INavigationService navigationService, IStatePersistanceService statePersistenceService)
    {
        NavigationService = navigationService;
        StatePersistenceService = statePersistenceService;
    }

    public INavigationService NavigationService { get; }
    public IStatePersistanceService StatePersistenceService { get; }
}
