using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class SyncAnimationControl : UserControl
{
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying),
        typeof(bool),
        typeof(SyncAnimationControl),
        new PropertyMetadata(true, OnIsPlayingChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public SyncAnimationControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsPlaying)
        {
            PlayAnimation();
        }
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SyncAnimationControl)d;

        if ((bool)e.NewValue)
        {
            control.PlayAnimation();
        }
        else
        {
            control.AnimationPlayer.Stop();
        }
    }

    private void PlayAnimation()
    {
#pragma warning disable CS4014 // Fire-and-forget is intentional for looped animation playback.
        AnimationPlayer.PlayAsync(0, 1, looped: true);
#pragma warning restore CS4014
    }
}
