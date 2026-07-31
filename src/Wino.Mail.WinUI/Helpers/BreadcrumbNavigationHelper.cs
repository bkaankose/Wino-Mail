using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Helpers;

public static class BreadcrumbNavigationHelper
{
    public static bool Navigate(
        Frame frame,
        ObservableCollection<BreadcrumbNavigationItemViewModel> pageHistory,
        BreadcrumbNavigationRequested message,
        Func<WinoPage, Type> getPageType)
    {
        var pageType = getPageType(message.PageType);

        if (pageType == null)
            return false;

        frame.Navigate(pageType, message.Parameter, new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromRight
        });

        SetActiveItem(pageHistory, null);
        pageHistory.Add(new BreadcrumbNavigationItemViewModel(
            message,
            isActive: true,
            stepNumber: pageHistory.Count + 1,
            backStackDepth: frame.BackStack.Count + 1));

        return true;
    }

    public static bool GoBack(
        Frame frame,
        ObservableCollection<BreadcrumbNavigationItemViewModel> pageHistory,
        NavigationTransitionEffect slideEffect)
    {
        if (!frame.CanGoBack || pageHistory.Count == 0)
            return false;

        pageHistory.RemoveAt(pageHistory.Count - 1);
        frame.GoBack(new SlideNavigationTransitionInfo
        {
            Effect = slideEffect == NavigationTransitionEffect.FromLeft
                ? SlideNavigationTransitionEffect.FromLeft
                : SlideNavigationTransitionEffect.FromRight
        });

        SetActiveItem(pageHistory, pageHistory.Count > 0 ? pageHistory[^1] : null);
        return true;
    }

    public static bool NavigateTo(
        Frame frame,
        ObservableCollection<BreadcrumbNavigationItemViewModel> pageHistory,
        int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= pageHistory.Count)
            return false;

        var activeIndex = GetActiveIndex(pageHistory);
        if (activeIndex <= 0 || targetIndex >= activeIndex)
            return false;

        var targetItem = pageHistory[targetIndex];

        while (frame.BackStack.Count > targetItem.BackStackDepth)
        {
            frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
        }

        if (!frame.CanGoBack)
            return false;

        frame.GoBack(new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromLeft
        });

        while (pageHistory.Count > targetIndex + 1)
        {
            pageHistory.RemoveAt(pageHistory.Count - 1);
        }

        SetActiveItem(pageHistory, targetItem);
        return true;
    }

    private static int GetActiveIndex(ObservableCollection<BreadcrumbNavigationItemViewModel> pageHistory)
    {
        for (var i = 0; i < pageHistory.Count; i++)
        {
            if (pageHistory[i].IsActive)
                return i;
        }

        return -1;
    }

    private static void SetActiveItem(
        ObservableCollection<BreadcrumbNavigationItemViewModel> pageHistory,
        BreadcrumbNavigationItemViewModel? activeItem)
    {
        foreach (var item in pageHistory)
        {
            item.IsActive = ReferenceEquals(item, activeItem);
        }
    }
}
