namespace Wino.Presentation.Linux.Lists;

/// <summary>The one GObject item type of every list model: it carries a managed object through a <see cref="Gio.ListStore"/>.</summary>
// The subclass generator rejects sealed classes (its constructor is protected internal) and parameterised constructors,
// so the value is set by Create after the generated constructor ran.
[GObject.Subclass<GObject.Object>]
public partial class ManagedItem
{
    public object? Value { get; private set; }

    public static ManagedItem Create(object value)
    {
        var item = NewWithProperties([]);
        item.Value = value;
        return item;
    }

    public static T? ValueOf<T>(GObject.Object? item) where T : class => (item as ManagedItem)?.Value as T;
}
