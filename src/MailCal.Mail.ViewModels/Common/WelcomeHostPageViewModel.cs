using MailCal.Core.Domain.Interfaces;

namespace MailCal.Core.ViewModels;

public partial class WelcomeHostPageViewModel : CoreBaseViewModel
{
    public WelcomeHostPageViewModel(INavigationService navigationService)
    {
        NavigationService = navigationService;
    }

    public INavigationService NavigationService { get; }
}
