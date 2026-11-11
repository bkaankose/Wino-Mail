using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;

namespace Wino.Dialogs;

public sealed partial class WinoAssistantDemoDialog : ContentDialog
{
    private static readonly Uri DemoVideoUri = new("ms-appx:///Assets/Intelligence/WinoAssistantDemo.mp4");

    private MediaSource? _demoSource;

    public WinoAssistantDemoDialog() => InitializeComponent();

    private void DialogLoaded(object sender, RoutedEventArgs e)
    {
        _demoSource ??= MediaSource.CreateFromUri(DemoVideoUri);
        DemoPlayer.Source = _demoSource;
    }

    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        DemoPlayer.MediaPlayer?.Pause();
        DemoPlayer.Source = null;
        _demoSource?.Dispose();
        _demoSource = null;
    }
}
