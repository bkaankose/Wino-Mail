namespace Wino.Presentation.Linux;

/// <summary>The only entry point for accessible names and descriptions: Gir.Core 0.8.1 has no public <c>Gtk.Accessible.UpdateProperty</c>.</summary>
public static class WinoA11y
{
    public static T Label<T>(T widget, string? label) where T : Gtk.Widget
    {
        if (string.IsNullOrEmpty(label)) widget.ResetProperty(Gtk.AccessibleProperty.Label);
        else Update(widget, Gtk.AccessibleProperty.Label, label);
        return widget;
    }

    /// <summary>
    /// The accessible name of a widget whose name GTK derives from a child label (a labelled button, an Adw row): the
    /// labelled-by relation outranks the label property, so it is dropped first.
    /// </summary>
    public static T Rename<T>(T widget, string label) where T : Gtk.Widget
    {
        widget.ResetRelation(Gtk.AccessibleRelation.LabelledBy);
        return Label(widget, label);
    }

    public static void Description(Gtk.Widget widget, string? description)
    {
        if (string.IsNullOrEmpty(description)) widget.ResetProperty(Gtk.AccessibleProperty.Description);
        else Update(widget, Gtk.AccessibleProperty.Description, description);
    }

    /// <summary>For icon-only buttons: the accessible name and the tooltip carry the same text.</summary>
    public static T LabelWithTooltip<T>(T widget, string text) where T : Gtk.Widget
    {
        Label(widget, text);
        widget.SetTooltipText(text);
        return widget;
    }

    private static void Update(Gtk.Widget widget, Gtk.AccessibleProperty property, string text)
    {
        using var value = new GObject.Value(text);
        using var values = GObject.Internal.ValueArray2OwnedHandle.Create([value]);
        Gtk.Internal.Accessible.UpdateProperty(widget.Handle.DangerousGetHandle(), 1, [property], values);
    }
}
