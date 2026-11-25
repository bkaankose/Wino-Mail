namespace Wino.Presentation.Linux.Style;

/// <summary>
/// A content zone (macOS Revision 3): edge to edge on the view background, a floating rounded card while an ancestor carries
/// <see cref="WinoCssClass.HasBackdrop"/>. The surface class lets a custom theme recolour the zone by its Windows palette role.
/// </summary>
public static class WinoZone
{
    public static Adw.Bin Create(Gtk.Widget? child, WinoThemeSurface surface = WinoThemeSurface.Workspace)
    {
        var zone = Adw.Bin.New();
        zone.SetHexpand(true);
        zone.SetVexpand(true);
        Apply(zone, surface);
        zone.SetChild(child);
        return zone;
    }

    public static void Apply(Gtk.Widget widget, WinoThemeSurface surface = WinoThemeSurface.Workspace)
    {
        widget.AddCssClass(WinoCssClass.Zone);
        widget.AddCssClass(SurfaceClass(surface));
        widget.SetOverflow(Gtk.Overflow.Hidden);
    }

    public static string SurfaceClass(WinoThemeSurface surface) => WinoCssClass.SurfacePrefix + surface switch
    {
        WinoThemeSurface.Workspace => "workspace",
        WinoThemeSurface.ReadingPane => "reading-pane",
        WinoThemeSurface.MailListHeader => "mail-list-header",
        WinoThemeSurface.Navigation => "navigation",
        WinoThemeSurface.CalendarDefaultHour => "calendar-default-hour",
        WinoThemeSurface.CalendarWorkHour => "calendar-work-hour",
        _ => "workspace"
    };
}
