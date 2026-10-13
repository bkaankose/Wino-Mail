namespace Wino.Mail.Controls.Core;

/// <summary>Small text rules shared by the native controls.</summary>
public static class WinoControlText
{
    /// <summary>Largest count an unread pill spells out; higher counts show "999+".</summary>
    public const int MaxUnreadPillCount = 999;

    /// <summary>The Windows InfoBadge text: the count, or "999+" above <see cref="MaxUnreadPillCount"/>.</summary>
    public static string UnreadPill(int count) => count > MaxUnreadPillCount ? $"{MaxUnreadPillCount}+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>A title with its count in brackets when positive ("Focused (2)"); the title alone otherwise.</summary>
    public static string WithCount(string? title, int count)
    {
        var text = title ?? string.Empty;
        return count > 0 ? $"{text} ({count})" : text;
    }
}

/// <summary>Diffing helpers for views that keep their child views and update only what changed.</summary>
public static class WinoListDiff
{
    /// <summary>
    /// How many leading items of <paramref name="shown"/> and <paramref name="next"/> can keep their views: the length of
    /// the common prefix under <paramref name="canReuse"/>.
    /// </summary>
    public static int ReusablePrefix<TShown, TNext>(IReadOnlyList<TShown> shown, IReadOnlyList<TNext> next, Func<TShown, TNext, bool> canReuse)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(canReuse);

        int keep = 0;
        while (keep < shown.Count && keep < next.Count && canReuse(shown[keep], next[keep])) keep++;
        return keep;
    }
}

/// <summary>
/// Selection state of a pager over a list where index 0 is the newest item (the briefing's day strip):
/// clamped selection and which arrows are enabled.
/// </summary>
public static class WinoPagerState
{
    /// <summary>The index clamped to the items; -1 when there are none.</summary>
    public static int Clamp(int index, int count) => count <= 0 ? -1 : Math.Clamp(index, 0, count - 1);

    /// <summary>The newer arrow moves towards index 0.</summary>
    public static bool CanGoNewer(int index) => index > 0;

    /// <summary>The older arrow moves towards the end.</summary>
    public static bool CanGoOlder(int index, int count) => index >= 0 && index < count - 1;
}
