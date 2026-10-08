// Copyright (c) 0x5BFA. Licensed under the MIT license.
// Single-island adaptation of DesktopFlyouts.Wasdk/DesktopFlyout.xaml.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;

public sealed partial class CompanionFlyoutSurface : UserControl
{
    private readonly CompanionMicaBackdrop _backdrop = new();

    internal CompanionFlyoutSurface(FrameworkElement content)
    {
        InitializeComponent();
        SurfaceContent.Content = content;
        SurfaceBackdrop.SystemBackdrop = _backdrop;

        if (!BackdropControllerHelpers.IsAnyBackdropSupported())
            SurfaceFallbackBackground.Visibility = Visibility.Visible;
    }

    internal CompositeTransform AnimationTransform => SurfaceTransform;

    internal void ApplyBackdropTheme(ElementTheme theme) => _backdrop.SetTheme(theme);

    internal void Detach()
    {
        SurfaceBackdrop.SystemBackdrop = null;
        SurfaceContent.Content = null;
    }
}
