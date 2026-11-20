namespace Wino.Presentation.Linux;

public static class WinoMenus
{
    /// <summary>The action group prefix a <see cref="WinoMenu"/> is inserted under on its popover.</summary>
    public const string ActionPrefix = "wino-menu";

    private static int _liveMenus;

    /// <summary>Menus whose action handlers are still connected. Zero once every popover is closed.</summary>
    public static int LiveMenus => Volatile.Read(ref _liveMenus);

    public static WinoMenuBuilder Create() => new(new WinoMenuBuilder.Shared());

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

    internal static void Opened() => Interlocked.Increment(ref _liveMenus);

    internal static void Released() => Interlocked.Decrement(ref _liveMenus);
}

/// <summary>
/// Builds a <see cref="Gio.Menu"/> and the action group its items activate. Items are grouped in sections, which
/// GTK draws with separators between them. Every item gets its own action, so one menu never depends on the
/// application's action names.
/// </summary>
public sealed class WinoMenuBuilder
{
    internal sealed class Shared
    {
        public readonly Gio.SimpleActionGroup Group = Gio.SimpleActionGroup.New();
        public readonly List<(Gio.SimpleAction Action, IDisposable Subscription)> Handlers = [];
        public readonly Dictionary<string, RadioGroup> Radios = new(StringComparer.Ordinal);
        public int Next;

        public string Add(Gio.SimpleAction action, GObject.SignalHandler<Gio.SimpleAction, Gio.SimpleAction.ActivateSignalArgs> handler)
        {
            var subscription = GtkSignals.Connect<Gio.SimpleAction, Gio.SimpleAction.ActivateSignalArgs>(
                h => action.OnActivate += h, h => action.OnActivate -= h, handler);
            Group.AddAction(action);
            Handlers.Add((action, subscription));
            return action.GetName();
        }
    }

    internal sealed class RadioGroup(string actionName)
    {
        public string ActionName { get; } = actionName;
        public Dictionary<string, Action> Handlers { get; } = new(StringComparer.Ordinal);
    }

    private readonly Shared _shared;
    private readonly Gio.Menu _root = Gio.Menu.New();
    private Gio.Menu? _section;
    private string? _sectionLabel;
    private int _count;

    internal WinoMenuBuilder(Shared shared) => _shared = shared;

    public bool IsEmpty => _count == 0;

    public WinoMenuBuilder Item(string label, Action invoke, bool enabled = true, string? accelerator = null)
    {
        var action = Gio.SimpleAction.New(NextName("i"), null);
        action.SetEnabled(enabled);
        var name = _shared.Add(action, (_, _) => invoke());
        Append(label, name, null, accelerator);
        return this;
    }

    public WinoMenuBuilder Check(string label, bool isChecked, Action<bool> toggled, bool enabled = true, string? accelerator = null)
    {
        var action = Gio.SimpleAction.NewStateful(NextName("c"), null, GLib.Variant.NewBoolean(isChecked));
        action.SetEnabled(enabled);
        var name = _shared.Add(action, (sender, _) =>
        {
            var value = !(sender.GetState()?.GetBoolean() ?? false);
            sender.SetState(GLib.Variant.NewBoolean(value));
            toggled(value);
        });
        Append(label, name, null, accelerator);
        return this;
    }

    /// <summary>One choice of a mutually exclusive <paramref name="group"/>; the group's action is shared by its items.</summary>
    public WinoMenuBuilder Radio(string group, string label, bool isSelected, Action invoke, string? accelerator = null)
    {
        if (!_shared.Radios.TryGetValue(group, out var radio))
        {
            var action = Gio.SimpleAction.NewStateful(NextName("r"), GLib.VariantType.New("s"), GLib.Variant.NewString(string.Empty));
            var handlers = new RadioGroup(action.GetName());
            _shared.Add(action, (sender, args) =>
            {
                if (args.Parameter?.GetString(out _) is not { } id || !handlers.Handlers.TryGetValue(id, out var run)) return;
                sender.SetState(GLib.Variant.NewString(id));
                run();
            });
            radio = handlers;
            _shared.Radios[group] = radio;
        }

        var target = (_shared.Next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        radio.Handlers[target] = invoke;
        if (isSelected && _shared.Group.LookupAction(radio.ActionName) is Gio.SimpleAction selected) selected.SetState(GLib.Variant.NewString(target));
        Append(label, radio.ActionName, GLib.Variant.NewString(target), accelerator);
        return this;
    }

    /// <summary>A submenu with its own sections. A disabled submenu shows as a disabled item, since GTK cannot disable a submenu link.</summary>
    public WinoMenuBuilder Submenu(string label, Action<WinoMenuBuilder> populate, bool enabled = true)
    {
        if (!enabled) return Item(label, static () => { }, enabled: false);
        var child = new WinoMenuBuilder(_shared);
        populate(child);
        if (child.IsEmpty) return this;
        CurrentSection().AppendSubmenu(label, child.Model());
        _count++;
        return this;
    }

    /// <summary>Ends the current section; the next item starts a new one, with <paramref name="heading"/> when given.</summary>
    public WinoMenuBuilder Separator(string? heading = null)
    {
        CloseSection();
        _sectionLabel = heading;
        return this;
    }

    /// <summary>The menu and its actions. Insert it once, on one popover; dispose disconnects every handler.</summary>
    public WinoMenu Build() => new(Model(), _shared);

    private Gio.Menu Model()
    {
        CloseSection();
        return _root;
    }

    private string NextName(string kind) => kind + (_shared.Next++).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Append(string label, string actionName, GLib.Variant? target, string? accelerator)
    {
        var item = Gio.MenuItem.New(label, null);
        if (target is null) item.SetDetailedAction(WinoMenus.ActionPrefix + "." + actionName);
        else item.SetActionAndTargetValue(WinoMenus.ActionPrefix + "." + actionName, target);
        if (!string.IsNullOrEmpty(accelerator)) item.SetAttributeValue("accel", GLib.Variant.NewString(accelerator));
        CurrentSection().AppendItem(item);
        _count++;
    }

    private Gio.Menu CurrentSection() => _section ??= Gio.Menu.New();

    private void CloseSection()
    {
        if (_section is null) return;
        _root.AppendSection(_sectionLabel, _section);
        _section = null;
        _sectionLabel = null;
    }
}

/// <summary>A built menu: the model a popover shows and the action group its items activate.</summary>
public sealed class WinoMenu : IDisposable
{
    private readonly WinoMenuBuilder.Shared _shared;
    private bool _disposed;

    internal WinoMenu(Gio.Menu model, WinoMenuBuilder.Shared shared)
    {
        Model = model;
        _shared = shared;
        WinoMenus.Opened();
    }

    public Gio.Menu Model { get; }

    public Gio.SimpleActionGroup Actions => _shared.Group;

    public bool IsEmpty => Model.GetNItems() == 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (action, subscription) in _shared.Handlers)
        {
            subscription.Dispose();
            _shared.Group.RemoveAction(action.GetName());
        }
        _shared.Handlers.Clear();
        _shared.Radios.Clear();
        WinoMenus.Released();
    }
}
