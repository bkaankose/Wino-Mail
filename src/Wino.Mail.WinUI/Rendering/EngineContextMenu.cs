using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// The context menu over the mail view in the reader and the composer: a plain <see cref="MenuFlyout"/> drawn in the
/// theme of the message, which can differ from the app's (a light message in a dark app). The theme is set through
/// the presenter style, before the presenter is created: themed after it opens, only the text followed and the
/// background kept the app's theme.
/// </summary>
internal static class EngineContextMenu
{
    public static void Show(FrameworkElement target, Point position, IReadOnlyList<ContextFlyoutMenuEntry> entries, ElementTheme theme)
    {
        var flyout = new MenuFlyout();

        if (theme != ElementTheme.Default)
        {
            var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
            presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, theme));
            flyout.MenuFlyoutPresenterStyle = presenterStyle;
        }

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
