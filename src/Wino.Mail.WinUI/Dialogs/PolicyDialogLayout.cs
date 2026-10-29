using System;
using Microsoft.UI.Xaml;

namespace Wino.Dialogs;

/// <summary>
/// Fits a policy dialog's content to the window. The policy WebView2 takes the mouse wheel, so content taller than the
/// dialog cannot be scrolled down to the acknowledgement, which leaves the accept button disabled in short windows.
/// </summary>
internal static class PolicyDialogLayout
{
    private const double PreferredWidth = 720;
    private const double PreferredHeight = 560;
    private const double MinimumWidth = 280;
    private const double MinimumHeight = 240;

    // The dialog's padding, title and button row, and its distance from the window edges.
    private const double HorizontalChrome = 96;
    private const double VerticalChrome = 240;

    public static void Fit(FrameworkElement content, XamlRoot? root)
    {
        if (root is null)
            return;

        content.Width = Math.Clamp(root.Size.Width - HorizontalChrome, MinimumWidth, PreferredWidth);
        content.Height = Math.Clamp(root.Size.Height - VerticalChrome, MinimumHeight, PreferredHeight);
    }
}
