using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wino.Extensions;

public enum TransitionDirection
{
    TopToBottom,
    BottomToTop,
    LeftToRight,
    RightToLeft
}

public enum ClipAnimationDirection
{
    Top,
    Bottom,
    Left,
    Right
}

public enum AnimationAxis
{
    X,
    Y,
    Z
}

public enum AnimationType
{
    KeyFrame,
    Expression
}

public enum FlickDirection
{
    None,
    Up,
    Down,
    Left,
    Right
}

public enum ViewState
{
    Empty,
    Small,
    Big,
    Full
}

public enum Gesture
{
    Initial,
    Tap,
    Swipe
}

[Flags]
public enum VisualPropertyType
{
    None = 0,
    Opacity = 1 << 0,
    Offset = 1 << 1,
    Scale = 1 << 2,
    Size = 1 << 3,
    RotationAngleInDegrees = 1 << 4,
    All = ~0
}
