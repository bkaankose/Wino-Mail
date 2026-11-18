using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Common;

public partial class WelcomeHostPageViewModel : CoreBaseViewModel
{
    public WelcomeHostPageViewModel(IMessenger messenger, INavigationService navigationService) : base(messenger)
    {
        NavigationService = navigationService;
    }

    public INavigationService NavigationService { get; }
}
