using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Wino.Mail.WinUI.Helpers;

internal static class CommandSourceHelper
{
    /// <summary>
    /// Clears the command of every button and menu item below <paramref name="root"/>,
    /// including the ones inside the flyouts and popups that hang off those elements.
    /// </summary>
    /// <remarks>
    /// A XAML command source subscribes to <c>CanExecuteChanged</c> and only lets go when its
    /// command is replaced. When the command belongs to a view model that outlives the page,
    /// the subscription keeps the element alive for the rest of the session, and every later
    /// <c>NotifyCanExecuteChanged</c> still calls into it. Pages whose view model is a singleton
    /// call this when they go away.
    /// </remarks>
    public static void ReleaseCommands(DependencyObject? root)
    {
        if (root == null)
            return;

        var visited = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            if (!visited.Add(current))
                continue;

            switch (current)
            {
                case MenuFlyoutSubItem subItem:
                    foreach (var item in subItem.Items)
                        pending.Push(item);
                    break;
                case MenuFlyoutItem menuItem:
                    menuItem.ClearValue(MenuFlyoutItem.CommandProperty);
                    break;
                case SplitButton splitButton:
                    splitButton.ClearValue(SplitButton.CommandProperty);
                    PushFlyout(pending, splitButton.Flyout);
                    break;
                case ButtonBase button:
                    button.ClearValue(ButtonBase.CommandProperty);

                    if (button is Button { Flyout: { } buttonFlyout })
                        PushFlyout(pending, buttonFlyout);
                    break;
                case CommandBar commandBar:
                    foreach (var element in commandBar.PrimaryCommands)
                        if (element is DependencyObject primary) pending.Push(primary);
                    foreach (var element in commandBar.SecondaryCommands)
                        if (element is DependencyObject secondary) pending.Push(secondary);
                    break;
                case Popup { Child: { } popupChild }:
                    pending.Push(popupChild);
                    break;
            }

            if (current is FrameworkElement frameworkElement)
            {
                PushFlyout(pending, frameworkElement.ContextFlyout);
                PushFlyout(pending, FlyoutBase.GetAttachedFlyout(frameworkElement));
            }

            // Content that never got its template applied is not part of the visual tree yet.
            if (current is ContentControl { Content: DependencyObject content })
                pending.Push(content);

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < childCount; i++)
                pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private static void PushFlyout(Stack<DependencyObject> pending, FlyoutBase? flyout)
    {
        switch (flyout)
        {
            case MenuFlyout menuFlyout:
                foreach (var item in menuFlyout.Items)
                    pending.Push(item);
                break;
            case CommandBarFlyout commandBarFlyout:
                foreach (var element in commandBarFlyout.PrimaryCommands)
                    if (element is DependencyObject primary) pending.Push(primary);
                foreach (var element in commandBarFlyout.SecondaryCommands)
                    if (element is DependencyObject secondary) pending.Push(secondary);
                break;
            case Flyout { Content: { } flyoutContent }:
                pending.Push(flyoutContent);
                break;
        }
    }
}
