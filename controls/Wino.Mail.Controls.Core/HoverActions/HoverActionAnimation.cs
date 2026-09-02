namespace Wino.Mail.Controls.Core.HoverActions;

/// <summary>
/// The entrance animation played when hover actions become visible on a mail row.
/// </summary>
public enum HoverActionAnimation
{
    /// <summary>
    /// Fades in while scaling up from the resting position.
    /// </summary>
    Popup,

    /// <summary>
    /// Fades in while translating from the edge implied by <see cref="HoverActionPosition"/>.
    /// </summary>
    Slide,

    /// <summary>
    /// Snaps between the hidden and visible states without motion.
    /// </summary>
    NoAnimation,
}
