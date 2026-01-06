namespace Wino.Core.Domain.Enums;

/// <summary>
/// Specifies the animation effect to use during a slide navigation transition.
/// </summary>
public enum NavigationTransitionEffect
{
    /// <summary>
    /// The navigation transition effect starts from the left edge of the frame.
    /// </summary>
    FromLeft,

    /// <summary>
    /// The navigation transition effect starts from the right edge of the frame.
    /// </summary>
    FromRight,

    /// <summary>
    /// The navigation transition effect starts from the top edge of the frame.
    /// </summary>
    FromTop,

    /// <summary>
    /// The navigation transition effect starts from the bottom edge of the frame.
    /// </summary>
    FromBottom
}
