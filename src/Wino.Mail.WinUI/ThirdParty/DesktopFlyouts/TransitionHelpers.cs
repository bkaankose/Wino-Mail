// Copyright (c) 0x5BFA. All rights reserved.
// Licensed under the MIT license.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;

internal static class TransitionHelpers
{
    internal static Storyboard GetWindows11BottomToTopTransitionStoryboard(DependencyObject target, double from, double to)
    {
        var storyboard = new Storyboard();

        var keyFrames = new DoubleAnimationUsingKeyFrames() { EnableDependentAnimation = true };
        keyFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame()
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0)),
            Value = from,
        });

        keyFrames.KeyFrames.Add(new SplineDoubleKeyFrame()
        {
            KeySpline = new() { ControlPoint1 = new(0.1, 0.9), ControlPoint2 = new(0.4, 1.0) },
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(267)),
            Value = to,
        });

        Storyboard.SetTarget(keyFrames, target);
        Storyboard.SetTargetProperty(keyFrames, "TranslateY");

        storyboard.Children.Add(keyFrames);

        return storyboard;
    }

    internal static Storyboard GetWindows11TopToBottomTransitionStoryboard(DependencyObject target, double from, double to)
    {
        var storyboard = new Storyboard();

        var keyFrames = new DoubleAnimationUsingKeyFrames() { EnableDependentAnimation = true };
        keyFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame()
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0)),
            Value = from,
        });

        keyFrames.KeyFrames.Add(new SplineDoubleKeyFrame()
        {
            KeySpline = new() { ControlPoint1 = new(0.2, 0.0), ControlPoint2 = new(0.9, 0.0) },
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
            Value = to,
        });

        Storyboard.SetTarget(keyFrames, target);
        Storyboard.SetTargetProperty(keyFrames, "TranslateY");

        storyboard.Children.Add(keyFrames);

        return storyboard;
    }

    internal static Storyboard GetWindows11RightToLeftTransitionStoryboard(DependencyObject target, double from, double to)
    {
        var storyboard = new Storyboard();

        var keyFrames = new DoubleAnimationUsingKeyFrames() { EnableDependentAnimation = true };
        keyFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame()
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0)),
            Value = from,
        });

        keyFrames.KeyFrames.Add(new SplineDoubleKeyFrame()
        {
            KeySpline = new() { ControlPoint1 = new(0.1, 0.9), ControlPoint2 = new(0.4, 1.0) },
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(167)),
            Value = to,
        });

        Storyboard.SetTarget(keyFrames, target);
        Storyboard.SetTargetProperty(keyFrames, "TranslateX");

        storyboard.Children.Add(keyFrames);

        return storyboard;
    }

    internal static Storyboard GetWindows11LeftToRightTransitionStoryboard(DependencyObject target, double from, double to)
    {
        var storyboard = new Storyboard();

        var keyFrames = new DoubleAnimationUsingKeyFrames() { EnableDependentAnimation = true };
        keyFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame()
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0)),
            Value = from,
        });

        keyFrames.KeyFrames.Add(new SplineDoubleKeyFrame()
        {
            KeySpline = new() { ControlPoint1 = new(0.2, 0.0), ControlPoint2 = new(0.9, 0.0) },
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(167)),
            Value = to,
        });

        Storyboard.SetTarget(keyFrames, target);
        Storyboard.SetTargetProperty(keyFrames, "TranslateX");

        storyboard.Children.Add(keyFrames);

        return storyboard;
    }

}
