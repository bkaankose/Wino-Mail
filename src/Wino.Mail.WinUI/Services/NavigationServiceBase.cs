using Microsoft.UI.Xaml.Media.Animation;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.WinUI.Services;

public class NavigationServiceBase
{
    public NavigationTransitionInfo GetNavigationTransitionInfo(NavigationTransitionType transition)
    {
        return transition switch
        {
            NavigationTransitionType.DrillIn => new DrillInNavigationTransitionInfo(),
            NavigationTransitionType.Entrance => new EntranceNavigationTransitionInfo(),
            _ => new SuppressNavigationTransitionInfo(),
        };
    }
}
