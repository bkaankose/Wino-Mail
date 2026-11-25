using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>One command of a reader command bar. Run receives the widget to anchor a menu or popover to.</summary>
internal sealed record MailReaderCommand(string Id, WinoIconGlyph Glyph, string Title, Action<Gtk.Widget> Run, bool IsEnabled = true, string? Accelerator = null,
    bool IsDestructive = false);

/// <summary>
/// The reader's command bar (macOS <c>MailReaderCommandBar</c>): glyph-and-label buttons while the labelled bar fits and
/// labels are wanted, glyphs only otherwise; commands that still do not fit move to the top of the More menu. The owner
/// supplies the width (<see cref="SetAvailableWidth"/>); the bar never reads its own allocation.
/// </summary>
internal sealed class MailReaderCommandBar : IDisposable
{
    private const int Spacing = 2;

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, Spacing);
    private readonly Gtk.ScrolledWindow _clip = Gtk.ScrolledWindow.New();
    private readonly Gtk.Box _row = Gtk.Box.New(Gtk.Orientation.Horizontal, Spacing);
    private readonly Gtk.Button _more;
    private readonly IDisposable _moreClicked;
    private readonly List<Slot> _slots = [];
    private Action<WinoMenuBuilder>? _moreItems;
    private int _available = -1;
    private int _moreWidth;
    private bool _showsLabels;
    private bool _labels;
    private int _shown = -1;
    private bool _measured;
    private bool _disposed;

    private sealed class Slot
    {
        public required Gtk.Widget View { get; init; }
        public Gtk.Button? Button { get; init; }
        public Gtk.Label? Text { get; init; }
        public Gtk.Label? Icon { get; init; }
        public IDisposable? Clicked { get; set; }
        public MailReaderCommand? Command { get; set; }
        public int LabelledWidth { get; set; }
        public int IconWidth { get; set; }
    }

    public MailReaderCommandBar()
    {
        WinoLiveObjects.Track(this);
        _root.AccessibleRole = Gtk.AccessibleRole.Toolbar;
        _root.SetValign(Gtk.Align.Center);
        // External: the bar's minimum width is about zero, so a stale layout clips for one frame instead of widening the pane.
        _clip.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        _clip.SetPropagateNaturalWidth(true);
        _clip.SetPropagateNaturalHeight(true);
        _clip.SetHasFrame(false);
        _clip.SetKineticScrolling(false);
        _clip.SetChild(_row);
        _more = WinoIcons.Button(WinoIconGlyph.More, Translator.More);
        _moreClicked = GtkSignals.OnClicked(_more, OpenMore);
        _root.Append(_clip);
        _root.Append(_more);
    }

    public Gtk.Widget Root => _root;
    public Gtk.Widget MoreButton => _more;
    public int ShownCount => _shown < 0 ? ButtonCount : _shown;
    public bool LabelsShown => _labels;

    public bool ShowsLabels
    {
        get => _showsLabels;
        set
        {
            if (_showsLabels == value) return;
            _showsLabels = value;
            Measure();
            Apply(force: true);
        }
    }

    private int ButtonCount => _slots.Count(static slot => slot.Button is not null);

    /// <summary>null entries are dividers. <paramref name="more"/> fills the More menu after the commands that did not fit.</summary>
    public void SetCommands(MailReaderCommand?[] commands, Action<WinoMenuBuilder>? more)
    {
        if (_disposed) return;
        _moreItems = more;
        if (SameShape(commands))
        {
            // The reader gets a new menu for every message and every flag change; rebuilding the buttons would drop focus.
            for (var index = 0; index < commands.Length; index++)
                if (commands[index] is { } command) Update(_slots[index], command);
        }
        else
        {
            Clear();
            foreach (var command in commands) _slots.Add(command is null ? Divider() : CommandSlot(command));
        }
        Measure();
        Apply(force: true);
    }

    public void SetAvailableWidth(int width)
    {
        width = Math.Max(0, width);
        if (!_measured) Measure();
        if (width == _available) return;
        _available = width;
        Apply(force: false);
    }

    /// <summary>The shown button of the command, else the More button: the anchor for its menu or popover.</summary>
    public Gtk.Widget AnchorFor(string id)
        => _slots.FirstOrDefault(slot => slot.Command?.Id == id && slot.View.GetVisible())?.Button ?? _more;

    public void OpenMore()
    {
        if (_disposed) return;
        var builder = WinoMenus.Create();
        var index = 0;
        foreach (var slot in _slots)
        {
            if (slot.Button is null) continue;
            if (index++ < ShownCount || slot.Command is not { } command) continue;
            builder.Item(command.Title, () => command.Run(_more), command.IsEnabled, command.Accelerator, command.Glyph, command.IsDestructive);
        }
        if (_moreItems is { } more)
        {
            builder.Separator();
            more(builder);
        }
        var menu = builder.Build();
        if (menu.IsEmpty)
        {
            menu.Dispose();
            return;
        }
        ContextMenu.Show(_more, menu);
    }

    public string Describe()
    {
        var lines = new List<string> { $"bar available={_available} shown={ShownCount}/{ButtonCount} labels={_labels} more={_more.GetVisible()}" };
        foreach (var slot in _slots)
            lines.Add(slot.Command is { } command ? $"  {command.Id} \"{command.Title}\" enabled={command.IsEnabled} visible={slot.View.GetVisible()}" : $"  | visible={slot.View.GetVisible()}");
        return string.Join('\n', lines);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _moreClicked.Dispose();
        foreach (var slot in _slots) slot.Clicked?.Dispose();
        _slots.Clear();
        _moreItems = null;
    }

    private bool SameShape(MailReaderCommand?[] commands)
    {
        if (commands.Length != _slots.Count) return false;
        for (var index = 0; index < commands.Length; index++)
            if ((commands[index] is null) != (_slots[index].Button is null)) return false;
        return true;
    }

    private void Clear()
    {
        foreach (var slot in _slots)
        {
            slot.Clicked?.Dispose();
            _row.Remove(slot.View);
        }
        _slots.Clear();
        _shown = -1;
    }

    private Slot Divider()
    {
        var divider = Gtk.Separator.New(Gtk.Orientation.Vertical);
        divider.SetMarginStart(6);
        divider.SetMarginEnd(6);
        divider.SetMarginTop(8);
        divider.SetMarginBottom(8);
        _row.Append(divider);
        return new Slot { View = divider };
    }

    private Slot CommandSlot(MailReaderCommand command)
    {
        var icon = WinoIcons.Label(command.Glyph, 16);
        var text = Gtk.Label.New(command.Title);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        content.Append(icon);
        content.Append(text);
        var button = Gtk.Button.New();
        button.SetChild(content);
        button.AddCssClass("flat");
        button.SetValign(Gtk.Align.Center);
        _row.Append(button);
        var slot = new Slot { View = button, Button = button, Text = text, Icon = icon };
        slot.Clicked = GtkSignals.OnClicked(button, () =>
        {
            if (slot.Command is { IsEnabled: true } current) current.Run(button);
        });
        Update(slot, command);
        return slot;
    }

    private static void Update(Slot slot, MailReaderCommand command)
    {
        var previous = slot.Command;
        slot.Command = command;
        if (slot.Button is not { } button) return;
        if (previous?.Glyph != command.Glyph && slot.Icon is { } icon) WinoIcons.SetGlyph(icon, WinoIcons.Glyph(command.Glyph));
        if (previous?.Title != command.Title)
        {
            slot.Text?.SetText(command.Title);
            WinoA11y.Rename(button, command.Title);
            button.SetTooltipText(command.Accelerator is { } accelerator && ShortcutKeys.TryParseAccelerator(accelerator, out var key, out var modifiers)
                ? $"{command.Title} ({Gtk.Functions.AcceleratorGetLabel(key, modifiers)})"
                : command.Title);
        }
        button.SetSensitive(command.IsEnabled);
    }

    private void Measure()
    {
        _measured = _root.GetRoot() is not null;
        foreach (var slot in _slots)
        {
            slot.View.SetVisible(true);
            if (slot.Button is not { } button || slot.Text is not { } text)
            {
                slot.View.Measure(Gtk.Orientation.Horizontal, -1, out _, out var natural, out _, out _);
                slot.LabelledWidth = slot.IconWidth = natural;
                continue;
            }
            text.SetVisible(true);
            button.Measure(Gtk.Orientation.Horizontal, -1, out _, out var labelled, out _, out _);
            text.SetVisible(false);
            button.Measure(Gtk.Orientation.Horizontal, -1, out _, out var iconOnly, out _, out _);
            slot.LabelledWidth = labelled;
            slot.IconWidth = iconOnly;
        }
        _more.SetVisible(true);
        _more.Measure(Gtk.Orientation.Horizontal, -1, out _, out var more, out _, out _);
        _moreWidth = more;
        _shown = -1;
    }

    private int Required(bool labels, int shown)
    {
        var total = ButtonCount;
        var width = 0;
        var children = 0;
        var buttons = 0;
        foreach (var slot in _slots)
        {
            if (slot.Button is null)
            {
                if (buttons > 0 && buttons < shown)
                {
                    width += slot.LabelledWidth;
                    children++;
                }
                continue;
            }
            if (buttons++ >= shown) continue;
            width += labels ? slot.LabelledWidth : slot.IconWidth;
            children++;
        }
        if (shown < total || _moreItems is not null)
        {
            width += _moreWidth + Spacing;
        }
        return width + Math.Max(0, children - 1) * Spacing;
    }

    private void Apply(bool force)
    {
        if (_disposed) return;
        var total = ButtonCount;
        var available = _available < 0 ? int.MaxValue : _available;
        var labels = _showsLabels && Required(true, total) <= available;
        var shown = total;
        if (!labels)
            while (shown > 0 && Required(false, shown) > available) shown--;
        if (!force && labels == _labels && shown == _shown) return;
        _labels = labels;
        _shown = shown;
        var buttons = 0;
        foreach (var slot in _slots)
        {
            if (slot.Button is null)
            {
                slot.View.SetVisible(buttons > 0 && buttons < shown);
                continue;
            }
            slot.Text?.SetVisible(labels);
            slot.View.SetVisible(buttons++ < shown);
        }
        _clip.SetVisible(shown > 0);
        _more.SetVisible(_moreItems is not null || shown < total);
        _clip.GetHadjustment().SetValue(0);
    }
}
