using Android.Widget;

namespace Wino.Presentation.Android;

/// <summary>Top app bar titles as TalkBack headings (the page title is the first heading on every screen).</summary>
public static class WinoToolbarTitle
{
    /// <summary>
    /// Sets <paramref name="title"/> on <paramref name="toolbar"/> and marks its title view as a heading. The toolbar
    /// creates the title view on the first non-empty title, so the mark follows every assignment.
    /// </summary>
    public static void SetHeading(this AndroidX.AppCompat.Widget.Toolbar toolbar, string? title)
    {
        toolbar.Title = title;
        if (string.IsNullOrEmpty(title)) return;
        for (int index = 0; index < toolbar.ChildCount; index++)
        {
            if (toolbar.GetChildAt(index) is TextView view && view.Text == title)
            {
                AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(view, true);
                return;
            }
        }
    }
}
