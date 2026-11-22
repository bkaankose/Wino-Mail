namespace Wino.Presentation.Linux.Style;

/// <summary>
/// The container behind the window content that the theme CSS paints with the wallpaper or gradient. It is a container,
/// not a sibling layer: CSS paints its background under the child, and descendants are styled through
/// <c>.wino-has-backdrop &lt;selector&gt;</c> while <see cref="WinoAppearance.HasBackdrop"/> is set.
/// </summary>
public sealed class ThemeBackdrop
{
    public ThemeBackdrop()
    {
        Root = Adw.Bin.New();
        Root.AddCssClass(WinoCssClass.ThemeBackdrop);
        Root.SetHexpand(true);
        Root.SetVexpand(true);
        WinoAppearance.Changed.Add(this, static self => self.Refresh());
        Refresh();
    }

    public Adw.Bin Root { get; }

    public Gtk.Widget? Child
    {
        get => Root.GetChild();
        set => Root.SetChild(value);
    }

    private void Refresh()
    {
        if (WinoAppearance.HasBackdrop) Root.AddCssClass(WinoCssClass.HasBackdrop);
        else Root.RemoveCssClass(WinoCssClass.HasBackdrop);
    }
}
