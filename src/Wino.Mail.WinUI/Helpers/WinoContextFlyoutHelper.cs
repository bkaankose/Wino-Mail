using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Helpers;

internal static class WinoContextFlyoutHelper
{
    public static void Show(
        FrameworkElement target,
        ContextRequestedEventArgs args,
        IReadOnlyList<ContextFlyoutMenuEntry> items,
        FlyoutPlacementMode placement = FlyoutPlacementMode.Auto)
    {
        args.Handled = true;

        var options = new FlyoutShowOptions { Placement = placement };
        if (args.TryGetPosition(target, out var position))
        {
            options.Position = position;
        }

        new WinoContextFlyout { ItemsSource = items }.ShowAt(target, options);
    }
}
