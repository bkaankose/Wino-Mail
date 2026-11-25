using System.Globalization;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux.Style;

namespace Wino.Presentation.Linux;

/// <summary>
/// One open menu: the root popover and its open submenu chain. Rows are buttons with menu-item roles, so assistive
/// technology can name and click them. Created by <see cref="ContextMenu.Show"/>.
/// </summary>
public sealed class WinoMenuPopover
{
    private const uint HoverDelay = 220;
    private const int MaximumHeight = 560;

    private readonly WinoMenu _menu;
    private readonly Level _root;
    private readonly List<Level> _levels = [];
    private readonly Action? _onClosed;
    private readonly Action _finished;
    private readonly MainLoop.Throttle _typeAheadReset;
    private readonly int _maxHeight;
    private string _typeAhead = string.Empty;
    private IDisposable? _hover;
    private Action? _chosen;
    private bool _closing;
    private bool _rootClosed;

    internal WinoMenuPopover(Gtk.Widget anchor, WinoMenu menu, Action? onClosed, Action finished)
    {
        _menu = menu;
        _onClosed = onClosed;
        _finished = finished;
        _typeAheadReset = new MainLoop.Throttle(1000, () => _typeAhead = string.Empty);
        _maxHeight = MaxHeightFor(anchor);
        WinoLiveObjects.Track(this);
        _root = CreateLevel(menu.Nodes, null);
    }

    /// <summary>The root popover, for positioning and screenshots. Do not add children.</summary>
    public Gtk.Popover Root => _root.Popover;

    public bool IsOpen => !_rootClosed;

    /// <summary>The popover of the deepest open submenu, or null when only the root is open.</summary>
    public Gtk.Popover? Submenu => Deepest() is { } level && level != _root ? level.Popover : null;

    /// <summary>Open levels; 1 = only the root.</summary>
    public int Depth
    {
        get
        {
            var depth = 0;
            for (var level = _root; level is not null && !level.IsClosed; level = level.Child) depth++;
            return depth;
        }
    }

    /// <summary>Closes the whole chain; no item runs.</summary>
    public void Popdown()
    {
        _chosen = null;
        CloseAll();
    }

    /// <summary>Runs the item at a label path ("Move to/Inbox") exactly as a click does; false when it is missing or disabled.</summary>
    public bool Activate(string path)
    {
        if (_rootClosed || string.IsNullOrEmpty(path)) return false;
        var parts = path.Split('/');
        var nodes = _menu.Nodes;
        for (var index = 0; index < parts.Length; index++)
        {
            var node = Array.Find(nodes, candidate => candidate.Kind != WinoMenuNodeKind.Separator && candidate.Label == parts[index]);
            if (node is null || !node.Enabled) return false;
            if (index < parts.Length - 1)
            {
                if (node.Kind != WinoMenuNodeKind.Submenu) return false;
                nodes = node.Children;
                continue;
            }
            if (node.Kind == WinoMenuNodeKind.Submenu) return false;
            Choose(node);
            return true;
        }
        return false;
    }

    /// <summary>Opens the submenu with that label in the deepest open level, as a click on its row does.</summary>
    public bool OpenSubmenu(string label)
    {
        if (Deepest() is not { } level) return false;
        var row = level.Rows.Find(candidate => candidate.Node.Kind == WinoMenuNodeKind.Submenu && candidate.Node.Label == label);
        if (row is null || !row.Node.Enabled) return false;
        OpenChild(row, focusFirst: true);
        return true;
    }

    /// <summary>The key handling of the deepest open level, as a key press there does it.</summary>
    public bool HandleKey(uint keyval, Gdk.ModifierType state) => Deepest() is { } level && HandleKey(level, keyval, state);

    /// <summary>One line per row of the open levels: depth|kind|label|accel|enabled|checked|destructive|focused|glyph.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        var depth = 1;
        for (var level = _root; level is not null && !level.IsClosed; level = level.Child, depth++)
        {
            for (var index = 0; index < level.Rows.Count; index++)
            {
                var row = level.Rows[index];
                var node = row.Node;
                text.Append(depth.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(node.Kind).Append('|')
                    .Append(node.Label).Append('|')
                    .Append(node.Accelerator is { } accel ? WinoMenus.AcceleratorLabel(accel) : string.Empty).Append('|')
                    .Append(node.Enabled).Append('|')
                    .Append(node.Checked).Append('|')
                    .Append(node.Destructive).Append('|')
                    .Append(level.Focus == index).Append('|')
                    .Append(node.Icon.IsEmpty ? "-" : "glyph")
                    .Append('\n');
            }
        }
        return text.ToString();
    }

    internal void Show(bool keyboard)
    {
        _root.Popover.Popup();
        if (keyboard) MainLoop.Post(() => FocusStep(_root, 0, 1));
    }

    private static int MaxHeightFor(Gtk.Widget anchor)
    {
        if (anchor.GetNative()?.GetSurface() is not { } surface || Gdk.Display.GetDefault()?.GetMonitorAtSurface(surface) is not { } monitor) return MaximumHeight;
        monitor.GetGeometry(out var geometry);
        return geometry.Height <= 0 ? MaximumHeight : Math.Min(MaximumHeight, Math.Max(160, geometry.Height - 48));
    }

    private Level CreateLevel(WinoMenuNode[] nodes, Row? parentRow)
    {
        var popover = Gtk.Popover.New();
        popover.SetHasArrow(false);
        popover.AddCssClass("menu");
        popover.AddCssClass(WinoCssClass.Menu);
        popover.SetAutohide(true);
        popover.SetCascadePopdown(false);

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        // The role can only be set before the widget is realized.
        box.AccessibleRole = Gtk.AccessibleRole.Menu;
        if (parentRow is not null) WinoA11y.Label(box, parentRow.Node.Label);

        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetPropagateNaturalHeight(true);
        scroller.SetPropagateNaturalWidth(true);
        scroller.SetMaxContentHeight(_maxHeight);
        scroller.SetChild(box);
        popover.SetChild(scroller);

        var level = new Level(popover, box, parentRow);
        _levels.Add(level);
        var hasMarks = Array.Exists(nodes, static node => node.Kind is WinoMenuNodeKind.Check or WinoMenuNodeKind.Radio);
        var hasIcons = Array.Exists(nodes, static node => node.Kind != WinoMenuNodeKind.Separator && !node.Icon.IsEmpty);
        foreach (var node in nodes)
        {
            if (node.Kind == WinoMenuNodeKind.Separator)
            {
                if (box.GetFirstChild() is not null)
                {
                    var separator = Gtk.Separator.New(Gtk.Orientation.Horizontal);
                    separator.SetMarginTop(6);
                    separator.SetMarginBottom(6);
                    box.Append(separator);
                }
                if (node.Label.Length > 0) box.Append(Heading(node.Label));
                continue;
            }
            var row = CreateRow(level, node, hasMarks, hasIcons);
            level.Rows.Add(row);
            box.Append(row.Button);
        }

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        level.Subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => HandleKey(level, args.Keyval, args.State)));
        popover.AddController(keys);
        level.Subscriptions.Add(GtkSignals.Connect<Gtk.Popover>(h => popover.OnClosed += h, h => popover.OnClosed -= h, (_, _) => LevelClosed(level)));
        return level;
    }

    private static Gtk.Label Heading(string text)
    {
        var heading = Gtk.Label.New(null);
        heading.SetText(text);
        heading.SetXalign(0);
        heading.AddCssClass("caption-heading");
        heading.AddCssClass("dim-label");
        heading.SetMarginTop(6);
        heading.SetMarginBottom(2);
        heading.SetMarginStart(12);
        heading.SetMarginEnd(12);
        heading.SetFocusable(false);
        return heading;
    }

    private Row CreateRow(Level level, WinoMenuNode node, bool hasMarks, bool hasIcons)
    {
        var button = Gtk.Button.New();
        button.AccessibleRole = node.Kind switch
        {
            WinoMenuNodeKind.Check => Gtk.AccessibleRole.MenuItemCheckbox,
            WinoMenuNodeKind.Radio => Gtk.AccessibleRole.MenuItemRadio,
            _ => Gtk.AccessibleRole.MenuItem,
        };
        button.AddCssClass("flat");
        button.AddCssClass(WinoCssClass.MenuItem);
        if (node.Destructive) button.AddCssClass(WinoCssClass.MenuDestructive);
        button.SetFocusOnClick(false);
        button.SetSensitive(node.Enabled);

        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        if (hasMarks)
        {
            var mark = WinoIcons.Label(WinoIconGlyph.Checkmark, 12);
            mark.SetSizeRequest(12, -1);
            mark.SetOpacity(node.Checked ? 1 : 0);
            content.Append(mark);
        }
        if (hasIcons)
        {
            var icon = WinoIcons.Label(string.Empty, 16);
            icon.SetSizeRequest(16, -1);
            if (node.Icon.Glyph is { Length: > 0 } glyph) WinoIcons.SetGlyph(icon, glyph, node.Icon.TintHex);
            content.Append(icon);
        }
        var text = Gtk.Label.New(null);
        text.SetText(node.Label);
        text.SetXalign(0);
        text.SetHexpand(true);
        text.SetEllipsize(Pango.EllipsizeMode.End);
        text.SetMaxWidthChars(48);
        content.Append(text);

        string? accelerator = null;
        if (node.Accelerator is { Length: > 0 } accel)
        {
            accelerator = WinoMenus.AcceleratorLabel(accel);
            var hint = Gtk.Label.New(null);
            hint.SetText(accelerator);
            hint.AddCssClass("dim-label");
            hint.AddCssClass(WinoCssClass.MenuAccel);
            hint.SetMarginStart(24);
            content.Append(hint);
        }
        if (node.Kind == WinoMenuNodeKind.Submenu) content.Append(Gtk.Image.NewFromIconName("go-next-symbolic"));
        button.SetChild(content);

        WinoA11y.Label(button, node.Label);
        if (accelerator is not null) WinoA11y.Description(button, accelerator);
        if (node.Kind is WinoMenuNodeKind.Check or WinoMenuNodeKind.Radio) WinoA11y.Checked(button, node.Checked);
        if (node.Kind == WinoMenuNodeKind.Submenu) WinoA11y.HasPopup(button);

        var row = new Row(level, node, button);
        level.Subscriptions.Add(GtkSignals.OnClicked(button, () => Activate(row, byKey: false)));
        var motion = Gtk.EventControllerMotion.New();
        level.Subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerMotion, Gtk.EventControllerMotion.EnterSignalArgs>(
            h => motion.OnEnter += h, h => motion.OnEnter -= h, (_, _) => Hovered(row)));
        button.AddController(motion);
        return row;
    }

    private void Hovered(Row row)
    {
        var level = row.Owner;
        if (_closing || level.IsClosed) return;
        _hover?.Dispose();
        _hover = null;
        if (row.Node.Enabled) Focus(level, level.Rows.IndexOf(row));
        if (row.Node.Kind == WinoMenuNodeKind.Submenu && row.Node.Enabled)
        {
            if (level.Child?.ParentRow == row) return;
            _hover = MainLoop.Delay(HoverDelay, () => OpenChild(row, focusFirst: false));
        }
        else if (level.Child is { } open)
        {
            _hover = MainLoop.Delay(HoverDelay, () => CloseLevel(open));
        }
    }

    private void Activate(Row row, bool byKey)
    {
        if (_closing || !row.Node.Enabled) return;
        if (row.Node.Kind == WinoMenuNodeKind.Submenu)
        {
            if (!byKey && row.Owner.Child?.ParentRow == row) CloseLevel(row.Owner.Child);
            else OpenChild(row, byKey);
            return;
        }
        Choose(row.Node);
    }

    private void Choose(WinoMenuNode node)
    {
        switch (node.Kind)
        {
            case WinoMenuNodeKind.Check:
                var toggled = node.Toggled;
                var value = !node.Checked;
                _chosen = toggled is null ? null : () => toggled(value);
                break;
            default:
                _chosen = node.Invoke;
                break;
        }
        CloseAll();
    }

    private void OpenChild(Row row, bool focusFirst)
    {
        var level = row.Owner;
        if (_closing || level.IsClosed || row.Node.Kind != WinoMenuNodeKind.Submenu) return;
        if (level.Child is { } open)
        {
            if (open.ParentRow == row)
            {
                if (focusFirst) FocusStep(open, 0, 1);
                return;
            }
            CloseLevel(open);
        }
        var child = CreateLevel(row.Node.Children, row);
        child.Parent = level;
        level.Child = child;
        child.Popover.SetParent(row.Button);
        child.Popover.SetPosition(Gtk.PositionType.Right);
        child.Popover.SetValign(Gtk.Align.Start);
        child.Popover.SetOffset(0, -6);
        row.Button.AddCssClass(WinoCssClass.MenuOpen);
        child.Popover.Popup();
        if (focusFirst) MainLoop.Post(() => FocusStep(child, 0, 1));
    }

    private static void CloseLevel(Level level)
    {
        if (!level.IsClosed) level.Popover.Popdown();
    }

    private void CloseAll()
    {
        if (_closing) return;
        _closing = true;
        _hover?.Dispose();
        _hover = null;
        var chain = new List<Level>();
        for (var level = _root; level is not null; level = level.Child) chain.Add(level);
        for (var index = chain.Count - 1; index >= 0; index--) CloseLevel(chain[index]);
    }

    private void LevelClosed(Level level)
    {
        if (level.IsClosed) return;
        level.IsClosed = true;
        if (level.Child is { } child) CloseLevel(child);
        if (level.Parent is { } parent)
        {
            if (parent.Child == level) parent.Child = null;
            level.ParentRow?.Button.RemoveCssClass(WinoCssClass.MenuOpen);
            if (!_closing && !parent.IsClosed && level.ParentRow is { } row) Focus(parent, parent.Rows.IndexOf(row));
            // Unparenting a popover inside its own closed signal crashes GTK; the teardown waits a turn.
            MainLoop.Post(() =>
            {
                Release(level);
                _levels.Remove(level);
            });
            return;
        }

        _rootClosed = true;
        _closing = true;
        _hover?.Dispose();
        _hover = null;
        _onClosed?.Invoke();
        MainLoop.Post(Teardown);
    }

    // The chosen item runs only after the whole chain closed, so a dialog or popover it opens gets the focus and the grab.
    private void Teardown()
    {
        var chosen = _chosen;
        _chosen = null;
        for (var index = _levels.Count - 1; index >= 0; index--) Release(_levels[index]);
        _levels.Clear();
        _typeAheadReset.Dispose();
        try { chosen?.Invoke(); }
        catch (Exception exception)
        {
            if (GtkSignals.HandlerFailed is { } report) report(exception);
            else Console.Error.WriteLine(exception);
        }
        finally
        {
            _menu.Dispose();
            _finished();
        }
    }

    private static void Release(Level level)
    {
        if (level.IsReleased) return;
        level.IsReleased = true;
        foreach (var subscription in level.Subscriptions) subscription.Dispose();
        level.Subscriptions.Clear();
        level.Popover.Unparent();
    }

    private Level? Deepest()
    {
        if (_rootClosed) return null;
        var level = _root;
        while (level.Child is { IsClosed: false } child) level = child;
        return level;
    }

    private bool HandleKey(Level level, uint keyval, Gdk.ModifierType state)
    {
        if (_closing || level.IsClosed) return false;
        var rtl = level.Popover.GetDirection() == Gtk.TextDirection.Rtl;
        var shift = (state & Gdk.ModifierType.ShiftMask) != 0;
        var forward = rtl ? Gdk.Constants.KEY_Left : Gdk.Constants.KEY_Right;
        var back = rtl ? Gdk.Constants.KEY_Right : Gdk.Constants.KEY_Left;
        var focused = level.Focus >= 0 && level.Focus < level.Rows.Count ? level.Rows[level.Focus] : null;

        if (keyval is (uint)Gdk.Constants.KEY_Down or (uint)Gdk.Constants.KEY_KP_Down || (keyval == (uint)Gdk.Constants.KEY_Tab && !shift))
        {
            FocusStep(level, level.Focus + 1, 1);
            return true;
        }
        if (keyval is (uint)Gdk.Constants.KEY_Up or (uint)Gdk.Constants.KEY_KP_Up or (uint)Gdk.Constants.KEY_ISO_Left_Tab || (keyval == (uint)Gdk.Constants.KEY_Tab && shift))
        {
            FocusStep(level, level.Focus < 0 ? level.Rows.Count - 1 : level.Focus - 1, -1);
            return true;
        }
        if (keyval is (uint)Gdk.Constants.KEY_Home or (uint)Gdk.Constants.KEY_KP_Home)
        {
            FocusStep(level, 0, 1);
            return true;
        }
        if (keyval is (uint)Gdk.Constants.KEY_End or (uint)Gdk.Constants.KEY_KP_End)
        {
            FocusStep(level, level.Rows.Count - 1, -1);
            return true;
        }
        if (keyval == (uint)forward)
        {
            if (focused is { Node.Kind: WinoMenuNodeKind.Submenu, Node.Enabled: true }) OpenChild(focused, focusFirst: true);
            return true;
        }
        if (keyval == (uint)back)
        {
            if (level.Parent is null) return false;
            CloseLevel(level);
            return true;
        }
        var isSpace = keyval == (uint)Gdk.Constants.KEY_space;
        if (keyval is (uint)Gdk.Constants.KEY_Return or (uint)Gdk.Constants.KEY_KP_Enter or (uint)Gdk.Constants.KEY_ISO_Enter || (isSpace && _typeAhead.Length == 0))
        {
            if (focused is not null) Activate(focused, byKey: true);
            return true;
        }
        if ((state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask)) != 0) return false;
        var unicode = Gdk.Functions.KeyvalToUnicode(keyval);
        if (unicode < 0x20 || unicode == 0x7f || unicode > 0x10FFFF) return false;
        TypeAhead(level, char.ConvertFromUtf32((int)unicode));
        return true;
    }

    private void TypeAhead(Level level, string typed)
    {
        _typeAhead += typed;
        _typeAheadReset.Request();
        var repeated = _typeAhead.Length > 1 && _typeAhead.All(character => character == _typeAhead[0]);
        var prefix = repeated ? _typeAhead[..1] : _typeAhead;
        var count = level.Rows.Count;
        if (count == 0) return;
        var start = prefix.Length == _typeAhead.Length && _typeAhead.Length > 1 ? Math.Max(level.Focus, 0) : level.Focus + 1;
        for (var step = 0; step < count; step++)
        {
            var index = ((start + step) % count + count) % count;
            var row = level.Rows[index];
            if (row.Node.Enabled && row.Node.Label.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                Focus(level, index);
                return;
            }
        }
    }

    private void FocusStep(Level level, int start, int direction)
    {
        var count = level.Rows.Count;
        if (count == 0) return;
        for (var step = 0; step < count; step++)
        {
            var index = ((start + step * direction) % count + count) % count;
            if (level.Rows[index].Node.Enabled)
            {
                Focus(level, index);
                return;
            }
        }
    }

    private static void Focus(Level level, int index)
    {
        if (index < 0 || index >= level.Rows.Count || level.IsClosed) return;
        if (level.Focus >= 0 && level.Focus < level.Rows.Count) level.Rows[level.Focus].Button.RemoveCssClass(WinoCssClass.MenuCurrent);
        level.Focus = index;
        level.Rows[index].Button.AddCssClass(WinoCssClass.MenuCurrent);
        if (level.Popover.GetMapped()) level.Rows[index].Button.GrabFocus();
    }

    private sealed class Level(Gtk.Popover popover, Gtk.Box box, Row? parentRow)
    {
        public Gtk.Popover Popover { get; } = popover;
        public Gtk.Box Box { get; } = box;
        public Row? ParentRow { get; } = parentRow;
        public Level? Parent { get; set; }
        public Level? Child { get; set; }
        public List<Row> Rows { get; } = [];
        public List<IDisposable> Subscriptions { get; } = [];
        public int Focus { get; set; } = -1;
        public bool IsClosed { get; set; }
        public bool IsReleased { get; set; }
    }

    private sealed class Row(Level owner, WinoMenuNode node, Gtk.Button button)
    {
        public Level Owner { get; } = owner;
        public WinoMenuNode Node { get; } = node;
        public Gtk.Button Button { get; } = button;
    }
}
