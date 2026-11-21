using Wino.Core.Domain;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Compose;

public readonly record struct RecipientToken(string Address, string? Name);

/// <summary>
/// A recipient field: wrapping chips and a trailing entry (Windows TokenizingTextBox, Mac NSTokenField). Enter, Tab, a comma
/// or a semicolon commit the typed text; Backspace in an empty entry selects, then removes, the last chip; pasted lists are
/// split. The head parses and validates through <see cref="Parse"/> and <see cref="Validate"/> and owns the recipients.
/// </summary>
public sealed class WinoRecipientTokenBox : IDisposable
{
    private const uint KeyReturn = 0xff0d, KeyPadEnter = 0xff8d, KeyTab = 0xff09, KeyBackSpace = 0xff08, KeyComma = 0x2c, KeySemicolon = 0x3b;
    private static readonly char[] Separators = [',', ';', '\n', '\r'];
    private readonly Adw.WrapBox _root = Adw.WrapBox.New();
    private readonly List<WinoRecipientChip> _chips = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly string _dragGroup;
    private readonly string _accessibleLabel;
    private bool _splitting;
    private bool _disposed;
    private uint _focusIdle;

    public WinoRecipientTokenBox(string id, string accessibleLabel, string dragGroup)
    {
        WinoLiveObjects.Track(this);
        Id = id;
        _accessibleLabel = accessibleLabel;
        _dragGroup = dragGroup;
        _root.AddCssClass(WinoCssClass.RecipientBox);
        _root.SetChildSpacing(4);
        _root.SetLineSpacing(4);
        _root.SetHexpand(true);
        WinoA11y.Label(_root, accessibleLabel);

        Entry = Gtk.Entry.New();
        Entry.SetHasFrame(false);
        Entry.SetHexpand(true);
        Entry.SetWidthChars(12);
        Entry.AddCssClass(WinoCssClass.RecipientEntry);
        WinoA11y.Label(Entry, accessibleLabel);
        _root.Append(Entry);

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => HandleKey(args.Keyval, args.State), false));
        Entry.AddController(keys);

        var focus = Gtk.EventControllerFocus.New();
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => focus.OnLeave += h, h => focus.OnLeave -= h, (_, _) => ScheduleFocusLeft()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => focus.OnEnter += h, h => focus.OnEnter -= h, (_, _) => ClearChipSelection()));
        Entry.AddController(focus);
        _subscriptions.Add(GtkSignals.OnChanged(Entry, TextEdited));

        var drop = Gtk.DropTarget.New(GObject.Type.String, Gdk.DragAction.Move);
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.DropSignalArgs, bool>(
            h => drop.OnDrop += h, h => drop.OnDrop -= h, (_, args) => AcceptDrop(args.Value.GetString()), false));
        _root.AddController(drop);
    }

    public string Id { get; }
    public Gtk.Widget Root => _root;
    public Gtk.Entry Entry { get; }
    public string TypedText => Entry.GetText().Trim();
    public string[] Addresses => [.. _chips.Select(static chip => chip.Address)];
    public int ChipCount => _chips.Count;

    /// <summary>Typed text to an address and optional name; null when the text is not an address.</summary>
    public Func<string, RecipientToken?>? Parse { get; set; }

    /// <summary>False refuses a parsed recipient (the head reports why); the text stays in the entry.</summary>
    public Func<string, string?, bool>? Validate { get; set; }

    /// <summary>Runs before the box handles a key; true consumes it (suggestions claim Up, Down, Enter, Tab and Escape).</summary>
    public Func<uint, Gdk.ModifierType, bool>? KeyFilter { get; set; }

    public event Action<WinoRecipientTokenBox, string, string?>? Committed;
    public event Action<WinoRecipientTokenBox, string>? Removed;
    public event Action<WinoRecipientTokenBox, string, string>? MoveRequested;
    public event Action<WinoRecipientTokenBox, string>? Refused;
    public event Action<WinoRecipientTokenBox>? TextChanged;
    public event Action<WinoRecipientTokenBox>? FocusLeft;
    public event Action<WinoRecipientTokenBox, string, string?, string>? DroppedFrom;
    public event Action<WinoRecipientTokenBox, WinoRecipientChip, double?, double?>? ChipMenuRequested;

    public void SetRecipients(RecipientToken[] recipients)
    {
        if (_chips.Count == recipients.Length && _chips.Select(static c => (c.Address, c.DisplayName)).SequenceEqual(recipients.Select(static r => (r.Address, string.IsNullOrWhiteSpace(r.Name) || string.Equals(r.Name.Trim(), r.Address, StringComparison.OrdinalIgnoreCase) ? null : r.Name.Trim()))))
            return;
        foreach (var chip in _chips) RemoveChipWidget(chip);
        _chips.Clear();
        foreach (var recipient in recipients) AddChip(recipient.Address, recipient.Name);
    }

    /// <summary>Commits the typed text; true when nothing was typed or every part became a chip.</summary>
    public bool CommitTyped()
    {
        var text = Entry.GetText();
        if (string.IsNullOrWhiteSpace(text)) return true;
        var rest = CommitParts(text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        SetEntryText(rest);
        return rest.Length == 0;
    }

    public void FocusEntry() => Entry.GrabFocus();

    /// <summary>Backspace in an empty entry: the first press selects the last chip, the second removes it.</summary>
    public void HandleBackspace()
    {
        if (_chips.Count == 0) return;
        var last = _chips[^1];
        if (!last.IsSelected)
        {
            last.IsSelected = true;
            return;
        }
        RemoveChip(last);
    }

    /// <summary>Runs a chip menu command: copy, edit, remove, or a move target ("to", "cc", "bcc").</summary>
    public void RunChipCommand(int index, string command)
    {
        if (index < 0 || index >= _chips.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var chip = _chips[index];
        switch (command)
        {
            case "copy": _root.GetClipboard().SetText(chip.Address); break;
            case "edit": EditChip(chip); break;
            case "remove": RemoveChip(chip); break;
            default: MoveRequested?.Invoke(this, chip.Address, command); break;
        }
    }

    /// <summary>The drag payload of a chip: group, box, address and name on separate lines.</summary>
    public string DragPayload(int index) => Payload(_chips[index]);

    /// <summary>Accepts a payload as a drop would; false when it belongs to another group or to this box.</summary>
    public bool AcceptDrop(string? payload)
    {
        if (payload?.Split('\n') is not [var group, var source, var address, var name] || group != _dragGroup || source == Id || address.Length == 0) return false;
        DroppedFrom?.Invoke(this, address, name.Length == 0 ? null : name, source);
        return true;
    }

    private string Payload(WinoRecipientChip chip) => $"{_dragGroup}\n{Id}\n{chip.Address}\n{chip.DisplayName}";

    private bool HandleKey(uint keyval, Gdk.ModifierType state)
    {
        if (KeyFilter is { } filter && filter(keyval, state)) return true;
        var modified = (state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask)) != 0;
        switch (keyval)
        {
            case KeyReturn or KeyPadEnter when !modified && TypedText.Length > 0:
            case KeyTab when !modified && TypedText.Length > 0:
            case KeyComma or KeySemicolon when !modified:
                CommitTyped();
                return true;
            case KeyBackSpace when Entry.GetText().Length == 0 && Entry.GetPosition() == 0:
                HandleBackspace();
                return _chips.Count > 0;
            default:
                ClearChipSelection();
                return false;
        }
    }

    private void TextEdited()
    {
        if (_splitting) return;
        var text = Entry.GetText();
        Entry.RemoveCssClass("error");
        if (text.IndexOfAny(Separators) >= 0)
        {
            var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            SetEntryText(CommitParts(parts, refuseQuietly: true));
            return;
        }
        TextChanged?.Invoke(this);
    }

    private string CommitParts(string[] parts, bool refuseQuietly = false)
    {
        var rest = new List<string>();
        foreach (var part in parts)
        {
            if (Parse?.Invoke(part) is not { } token)
            {
                rest.Add(part);
                if (!refuseQuietly) Refused?.Invoke(this, part);
                continue;
            }
            if (Validate is { } validate && !validate(token.Address, token.Name))
            {
                rest.Add(part);
                continue;
            }
            AddChip(token.Address, token.Name);
            Committed?.Invoke(this, token.Address, token.Name);
        }
        if (rest.Count > 0) Entry.AddCssClass("error");
        return string.Join(", ", rest);
    }

    private void SetEntryText(string text)
    {
        _splitting = true;
        try
        {
            Entry.SetText(text);
            Entry.SetPosition(-1);
        }
        finally { _splitting = false; }
        TextChanged?.Invoke(this);
    }

    private void AddChip(string address, string? name)
    {
        var chip = new WinoRecipientChip(address, name);
        chip.RemoveRequested += RemoveChip;
        chip.Activated += static chip => chip.IsSelected = !chip.IsSelected;
        chip.MenuRequested += (sender, x, y) => ChipMenuRequested?.Invoke(this, sender, x, y);
        var source = Gtk.DragSource.New();
        source.SetActions(Gdk.DragAction.Move);
        chip.Own(GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.PrepareSignalArgs, Gdk.ContentProvider?>(
            h => source.OnPrepare += h, h => source.OnPrepare -= h, (_, _) => Gdk.ContentProvider.NewForValue(new GObject.Value(Payload(chip))), null));
        chip.Root.AddController(source);
        _chips.Add(chip);
        _root.Remove(Entry);
        _root.Append(chip.Root);
        _root.Append(Entry);
    }

    private void RemoveChip(WinoRecipientChip chip)
    {
        if (!_chips.Remove(chip)) return;
        RemoveChipWidget(chip);
        Removed?.Invoke(this, chip.Address);
    }

    private void RemoveChipWidget(WinoRecipientChip chip)
    {
        _root.Remove(chip.Root);
        chip.Dispose();
    }

    private void EditChip(WinoRecipientChip chip)
    {
        RemoveChip(chip);
        SetEntryText(chip.DisplayName is null ? chip.Address : $"{chip.DisplayName} <{chip.Address}>");
        Entry.GrabFocus();
        Entry.SelectRegion(0, -1);
    }

    private void ClearChipSelection()
    {
        foreach (var chip in _chips) chip.IsSelected = false;
    }

    // Focus moving to a chip button or a suggestion inside the same field is not leaving it.
    private void ScheduleFocusLeft()
    {
        if (_focusIdle != 0) return;
        _focusIdle = GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            _focusIdle = 0;
            if (_disposed) return false;
            if (_root.GetRoot() is Gtk.Window window && window.GetFocus() is { } focus && focus.IsAncestor(_root)) return false;
            FocusLeft?.Invoke(this);
            return false;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_focusIdle != 0) GLib.Functions.SourceRemove(_focusIdle);
        _focusIdle = 0;
        Committed = null; Removed = null; MoveRequested = null; Refused = null; TextChanged = null; FocusLeft = null; DroppedFrom = null; ChipMenuRequested = null;
        Parse = null; Validate = null; KeyFilter = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        foreach (var chip in _chips) chip.Dispose();
        _chips.Clear();
    }

    public override string ToString() => $"{_accessibleLabel}: {string.Join(", ", Addresses)} | typed='{TypedText}'";
}
