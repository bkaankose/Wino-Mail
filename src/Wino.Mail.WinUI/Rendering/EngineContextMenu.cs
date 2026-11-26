using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// The context menu over the mail view in the reader and the composer: a plain <see cref="MenuFlyout"/> drawn in the
/// theme of the message, which can differ from the app's (a light message in a dark app). A menu flyout's presenter
/// is themed once it exists, so its own style draws everything else.
/// </summary>
internal static class EngineContextMenu
{
    public static void Show(FrameworkElement target, Point position, IReadOnlyList<ContextFlyoutMenuEntry> entries, ElementTheme theme)
    {
        var flyout = new MenuFlyout();

        if (theme != ElementTheme.Default)
            flyout.Opened += (_, _) => ApplyTheme(flyout, theme);

        var previousWasSeparator = true;
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ContextFlyoutSeparatorEntry:
                    if (!previousWasSeparator)
                        flyout.Items.Add(new MenuFlyoutSeparator());

                    previousWasSeparator = true;
                    break;
                case ContextFlyoutCommandEntry command:
                    flyout.Items.Add(CreateItem(command));
                    previousWasSeparator = false;
                    break;
            }
        }

        if (flyout.Items.Count > 0 && previousWasSeparator)
            flyout.Items.RemoveAt(flyout.Items.Count - 1);

        if (flyout.Items.Count == 0)
            return;

        flyout.ShowAt(target, new FlyoutShowOptions { Position = position, ShowMode = FlyoutShowMode.Standard });
    }

    // The presenter is created when the menu opens; it is the nearest presenter above the items.
    [WinRT.DynamicWindowsRuntimeCast(typeof(MenuFlyoutPresenter))]
    private static void ApplyTheme(MenuFlyout flyout, ElementTheme theme)
    {
        if (flyout.Items.Count == 0)
            return;

        DependencyObject? current = flyout.Items[0];
        while (current is not null && current is not MenuFlyoutPresenter)
            current = VisualTreeHelper.GetParent(current);

        if (current is MenuFlyoutPresenter presenter && presenter.RequestedTheme != theme)
            presenter.RequestedTheme = theme;
    }

    private static MenuFlyoutItem CreateItem(ContextFlyoutCommandEntry entry)
    {
        var item = new MenuFlyoutItem
        {
            Text = entry.Text,
            Command = entry.Command,
            CommandParameter = entry.CommandParameter,
            IsEnabled = entry.IsEnabled
        };

        if (entry.Icon is { } icon)
            item.Icon = new WinoFontIcon { Glyph = icon.Glyph };

        if (entry.Shortcut is { } shortcut)
            item.KeyboardAcceleratorTextOverride = shortcut.DisplayText;

        if (!string.IsNullOrEmpty(entry.AutomationId))
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, entry.AutomationId);

        return item;
    }
}
