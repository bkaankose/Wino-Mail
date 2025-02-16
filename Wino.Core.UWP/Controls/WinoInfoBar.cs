using System;
using System.Numerics;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml;
using Wino.Core.Domain.Enums;

namespace Wino.Core.UWP.Controls
{
    public partial class WinoInfoBar : InfoBar
    {
        public static readonly DependencyProperty AnimationTypeProperty = DependencyProperty.Register(nameof(AnimationType), typeof(InfoBarAnimationType), typeof(WinoInfoBar), new PropertyMetadata(InfoBarAnimationType.SlideFromRightToLeft));
        public static readonly DependencyProperty DismissIntervalProperty = DependencyProperty.Register(nameof(DismissInterval), typeof(int), typeof(WinoInfoBar), new PropertyMetadata(5, new PropertyChangedCallback(OnDismissIntervalChanged)));

        public InfoBarAnimationType AnimationType
        {
            get { return (InfoBarAnimationType)GetValue(AnimationTypeProperty); }
            set { SetValue(AnimationTypeProperty, value); }
        }

        public int DismissInterval
        {
            get { return (int)GetValue(DismissIntervalProperty); }
            set { SetValue(DismissIntervalProperty, value); }
        }

        private readonly DispatcherTimer _dispatcherTimer = new DispatcherTimer();

        public WinoInfoBar()
        {
            RegisterPropertyChangedCallback(IsOpenProperty, IsOpenChanged);

            _dispatcherTimer.Interval = TimeSpan.FromSeconds(DismissInterval);
        }

        private static void OnDismissIntervalChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoInfoBar bar)
            {
                bar.UpdateInterval(bar.DismissInterval);
            }
        }

        private void UpdateInterval(int seconds) => _dispatcherTimer.Interval = TimeSpan.FromSeconds(seconds);

        private async void IsOpenChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (sender is WinoInfoBar control)
            {
                // Message shown.
                if (!control.IsOpen) return;

                Opacity = 1;

                _dispatcherTimer.Stop();

                _dispatcherTimer.Tick -= TimerTick;
                _dispatcherTimer.Tick += TimerTick;

                _dispatcherTimer.Start();

                // Slide from right.
                if (AnimationType == InfoBarAnimationType.SlideFromRightToLeft)
                {
                    await AnimationBuilder.Create().Translation(new Vector3(0, 0, 0), new Vector3(150, 0, 0), null, TimeSpan.FromSeconds(0.5)).StartAsync(this);
                }
                else if (AnimationType == InfoBarAnimationType.SlideFromBottomToTop)
                {
                    await AnimationBuilder.Create().Translation(new Vector3(0, 0, 0), new Vector3(0, 50, 0), null, TimeSpan.FromSeconds(0.5)).StartAsync(this);
                }
            }
        }

        private async void TimerTick(object sender, object e)
        {
            _dispatcherTimer.Stop();
            _dispatcherTimer.Tick -= TimerTick;

            if (AnimationType == InfoBarAnimationType.SlideFromRightToLeft)
            {
                await AnimationBuilder.Create().Translation(new Vector3((float)ActualWidth, 0, 0), new Vector3(0, 0, 0), null, TimeSpan.FromSeconds(0.5)).StartAsync(this);
            }
            else if (AnimationType == InfoBarAnimationType.SlideFromBottomToTop)
            {
                await AnimationBuilder.Create().Translation(new Vector3(0, (float)ActualHeight, 0), new Vector3(0, 0, 0), null, TimeSpan.FromSeconds(0.5)).StartAsync(this);
            }

            IsOpen = false;
        }
    }
}
