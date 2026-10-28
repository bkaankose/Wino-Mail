using CoreFoundation;
using UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Pages a shell moves from one navigation controller to another (the iPad's list and item columns on rotation). Leaving
/// the old one looks like a pop, so <see cref="WinoViewController{TViewModel}"/> keeps such a page alive for the move.
/// </summary>
public static class WinoPageMoves
{
    private static readonly HashSet<UIViewController> Moving = [];

    /// <summary>Marks <paramref name="controller"/> as moving until the appearance callbacks of the move have run.</summary>
    public static void Begin(UIViewController controller)
    {
        if (!Moving.Add(controller)) return;
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromSeconds(1)), () => Moving.Remove(controller));
    }

    public static bool IsMoving(UIViewController controller) => Moving.Contains(controller);
}
