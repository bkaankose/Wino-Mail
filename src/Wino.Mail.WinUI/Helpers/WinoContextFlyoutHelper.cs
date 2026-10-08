using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Helpers;

internal static class WinoContextFlyoutHelper
{
    public static void Show(
        FrameworkElement target,
        ContextRequestedEventArgs args,
        IReadOnlyList<ContextFlyoutMenuEntry> items,
        FlyoutPlacementMode placement = FlyoutPlacementMode.BottomEdgeAlignedLeft)
    {
        args.Handled = true;

        FlyoutShowOptions options;
        if (args.TryGetPosition(target, out var position))
        {
            options = CreatePointerAlignedOptions(position, placement);
        }
        else
        {
            options = new FlyoutShowOptions { Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        }

        new WinoContextFlyout { ItemsSource = items }.ShowAt(target, options);
    }

    public static FlyoutShowOptions CreatePointerAlignedOptions(
        Point position,
        FlyoutPlacementMode placement = FlyoutPlacementMode.BottomEdgeAlignedLeft)
        => new()
        {
            ShowMode = FlyoutShowMode.Standard,
            Placement = placement,
            Position = position
        };
}
