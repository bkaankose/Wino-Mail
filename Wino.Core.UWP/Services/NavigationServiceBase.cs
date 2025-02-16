using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.UWP.Services;

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

    public Type GetCurrentFrameType(ref Frame _frame)
    {
        if (_frame != null && _frame.Content != null)
            return _frame.Content.GetType();

        return null;
    }
}
