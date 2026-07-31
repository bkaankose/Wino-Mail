using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

public class WelcomeHostPageViewModel : CoreBaseViewModel
{
    public WelcomeHostPageViewModel(INavigationService navigationService)
    {
        NavigationService = navigationService;
    }

    public INavigationService NavigationService { get; }
}
