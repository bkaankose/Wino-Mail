namespace Wino.Mail.Linux.Infrastructure;

/// <summary>A page with its own find bar (the composer) that takes Ctrl+F while the keyboard focus is inside it.</summary>
internal interface IFindInPageTarget
{
    bool OwnsFocus(Gtk.Window window);
    void OpenFind();
}

/// <summary>
/// Ctrl+F is the application's search accelerator, which GTK runs before any view sees the key. The search action asks
/// here first, so a page with a find bar keeps the key while it has the focus.
/// </summary>
internal static class FindInPageTargets
{
    private static readonly List<WeakReference<IFindInPageTarget>> _targets = [];

    public static IDisposable Register(IFindInPageTarget target)
    {
        var reference = new WeakReference<IFindInPageTarget>(target);
        _targets.Add(reference);
        return new Wino.Presentation.Linux.ActionDisposable(() => _targets.Remove(reference));
    }

    public static bool TryOpen(Gtk.Window? window)
    {
        if (window is null) return false;
        _targets.RemoveAll(static reference => !reference.TryGetTarget(out _));
        foreach (var reference in _targets)
        {
            if (!reference.TryGetTarget(out var target) || !target.OwnsFocus(window)) continue;
            target.OpenFind();
            return true;
        }
        return false;
    }
}
