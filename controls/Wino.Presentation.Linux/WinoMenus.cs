using Wino.Core.Domain.Enums;

namespace Wino.Presentation.Linux;

/// <summary>A glyph of a menu item: a WinoIcons glyph and an optional <c>#RRGGBB</c> tint. The default is no glyph.</summary>
public readonly record struct WinoMenuIcon(string? Glyph, string? TintHex = null)
{
    public static implicit operator WinoMenuIcon(WinoIconGlyph icon)
        => icon == WinoIconGlyph.None ? default : new WinoMenuIcon(WinoIcons.Glyph(icon));

    public bool IsEmpty => string.IsNullOrEmpty(Glyph);
}

public static class WinoMenus
{
    private static int _liveMenus;

    /// <summary>Menus built and not disposed yet. Zero once every menu is closed.</summary>
    public static int LiveMenus => Volatile.Read(ref _liveMenus);

    public static WinoMenuBuilder Create() => new();

    /// <summary>
    /// A GTK accelerator string such as <c>&lt;Control&gt;&lt;Shift&gt;r</c> for a GDK key name, or null when GDK does
    /// not know the key name.
    /// </summary>
    public static string? Accelerator(string gdkKeyName, bool control = false, bool alt = false, bool shift = false, bool super = false)
    {
        if (string.IsNullOrWhiteSpace(gdkKeyName)) return null;
        var keyval = Gdk.Functions.KeyvalFromName(gdkKeyName);
        if (keyval == 0 || keyval == (uint)Gdk.Constants.KEY_VoidSymbol) return null;
        return (control ? "<Control>" : string.Empty)
            + (alt ? "<Alt>" : string.Empty)
            + (shift ? "<Shift>" : string.Empty)
            + (super ? "<Super>" : string.Empty)
            + gdkKeyName;
    }

    /// <summary>The display text of an accelerator string (<c>&lt;Control&gt;&lt;Shift&gt;r</c> is "Ctrl+Shift+R"); the input when it cannot be parsed.</summary>
    public static string AcceleratorLabel(string accelerator)
    {
        if (string.IsNullOrWhiteSpace(accelerator)) return string.Empty;
        var modifiers = (Gdk.ModifierType)0;
        var rest = accelerator.AsSpan().Trim();
        while (rest.Length > 0 && rest[0] == '<')
        {
            var end = rest.IndexOf('>');
            if (end < 0) return accelerator;
            var name = rest[1..end];
            if (name.Equals("Control", StringComparison.OrdinalIgnoreCase) || name.Equals("Primary", StringComparison.OrdinalIgnoreCase) || name.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= Gdk.ModifierType.ControlMask;
            else if (name.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= Gdk.ModifierType.ShiftMask;
            else if (name.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= Gdk.ModifierType.AltMask;
            else if (name.Equals("Super", StringComparison.OrdinalIgnoreCase)) modifiers |= Gdk.ModifierType.SuperMask;
            else return accelerator;
            rest = rest[(end + 1)..];
        }
        if (rest.Length == 0) return accelerator;
        var keyval = Gdk.Functions.KeyvalFromName(rest.ToString());
        if (keyval == 0 || keyval == (uint)Gdk.Constants.KEY_VoidSymbol) return accelerator;
        var label = Gtk.Functions.AcceleratorGetLabel(keyval, modifiers);
        return string.IsNullOrEmpty(label) ? accelerator : label;
    }

    internal static void Opened() => Interlocked.Increment(ref _liveMenus);

    internal static void Released() => Interlocked.Decrement(ref _liveMenus);
}

internal enum WinoMenuNodeKind
{
    Item,
    Check,
    Radio,
    Submenu,
    Separator,
}

internal sealed class WinoMenuNode(WinoMenuNodeKind kind, string label)
{
    public WinoMenuNodeKind Kind { get; } = kind;
    public string Label { get; } = label;
    public string? Accelerator { get; init; }
    public WinoMenuIcon Icon { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Destructive { get; init; }
    public bool Checked { get; init; }
    public Action? Invoke { get; set; }
    public Action<bool>? Toggled { get; set; }
    public WinoMenuNode[] Children { get; init; } = [];

    public void Release()
    {
        Invoke = null;
        Toggled = null;
        foreach (var child in Children) child.Release();
    }
}

/// <summary>
/// Builds a menu as plain data: items with an optional glyph and accelerator, check and radio items, submenus and
/// separators with optional headings. <see cref="ContextMenu.Show"/> draws it in a <see cref="WinoMenuPopover"/>.
/// </summary>
public sealed class WinoMenuBuilder
{
    private readonly List<WinoMenuNode> _nodes = [];
    private bool _separatorPending;
    private string? _pendingHeading;

    internal WinoMenuBuilder()
    {
    }

    public bool IsEmpty => !_nodes.Exists(static node => node.Kind != WinoMenuNodeKind.Separator);

    public WinoMenuBuilder Item(string label, Action invoke, bool enabled = true, string? accelerator = null,
        WinoMenuIcon icon = default, bool destructive = false)
        => Add(new WinoMenuNode(WinoMenuNodeKind.Item, label)
        {
            Invoke = invoke,
            Enabled = enabled,
            Accelerator = accelerator,
            Icon = icon,
            Destructive = destructive,
        });

    public WinoMenuBuilder Check(string label, bool isChecked, Action<bool> toggled, bool enabled = true, string? accelerator = null,
        WinoMenuIcon icon = default)
        => Add(new WinoMenuNode(WinoMenuNodeKind.Check, label)
        {
            Toggled = toggled,
            Checked = isChecked,
            Enabled = enabled,
            Accelerator = accelerator,
            Icon = icon,
        });

    /// <summary>One choice of a mutually exclusive group; <paramref name="group"/> only names the group, <paramref name="invoke"/> runs on click.</summary>
    public WinoMenuBuilder Radio(string group, string label, bool isSelected, Action invoke, string? accelerator = null,
        WinoMenuIcon icon = default, bool enabled = true)
        => Add(new WinoMenuNode(WinoMenuNodeKind.Radio, label)
        {
            Invoke = invoke,
            Checked = isSelected,
            Enabled = enabled,
            Accelerator = accelerator,
            Icon = icon,
        });

    /// <summary>A submenu. An empty one is left out; a disabled one is a disabled item without children.</summary>
    public WinoMenuBuilder Submenu(string label, Action<WinoMenuBuilder> populate, bool enabled = true, WinoMenuIcon icon = default)
    {
        if (!enabled) return Add(new WinoMenuNode(WinoMenuNodeKind.Item, label) { Enabled = false, Icon = icon });
        var child = new WinoMenuBuilder();
        populate(child);
        if (child.IsEmpty) return this;
        return Add(new WinoMenuNode(WinoMenuNodeKind.Submenu, label) { Icon = icon, Children = child.Nodes() });
    }

    /// <summary>Starts a new group; the next item draws a line before it, and <paramref name="heading"/> when given.</summary>
    public WinoMenuBuilder Separator(string? heading = null)
    {
        _separatorPending = true;
        _pendingHeading = string.IsNullOrEmpty(heading) ? null : heading;
        return this;
    }

    public WinoMenu Build() => new(Nodes());

    private WinoMenuBuilder Add(WinoMenuNode node)
    {
        if (_separatorPending && (_nodes.Count > 0 || _pendingHeading is not null))
        {
            _nodes.Add(new WinoMenuNode(WinoMenuNodeKind.Separator, _pendingHeading ?? string.Empty));
        }
        _separatorPending = false;
        _pendingHeading = null;
        _nodes.Add(node);
        return this;
    }

    private WinoMenuNode[] Nodes() => [.. _nodes];
}

/// <summary>A built menu. <see cref="ContextMenu.Show"/> owns it from then on; dispose drops every delegate.</summary>
public sealed class WinoMenu : IDisposable
{
    private bool _disposed;

    internal WinoMenu(WinoMenuNode[] nodes)
    {
        Nodes = nodes;
        WinoMenus.Opened();
        WinoLiveObjects.Track(this);
    }

    internal WinoMenuNode[] Nodes { get; }

    public bool IsEmpty => Count == 0;

    /// <summary>Top-level entries without separators.</summary>
    public int Count
    {
        get
        {
            var count = 0;
            foreach (var node in Nodes)
            {
                if (node.Kind != WinoMenuNodeKind.Separator) count++;
            }
            return count;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var node in Nodes) node.Release();
        WinoMenus.Released();
    }
}
