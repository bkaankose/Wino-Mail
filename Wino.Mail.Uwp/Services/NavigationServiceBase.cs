using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.Uwp.Services;

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

    public Type? GetCurrentFrameType(Frame frame)
    {
        if (frame != null && frame.Content != null)
            return frame.Content.GetType();

        return null;
    }
}
