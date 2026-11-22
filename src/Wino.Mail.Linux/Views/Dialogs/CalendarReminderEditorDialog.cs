using System.Collections.Specialized;
using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.ViewModels.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The event reminders editor (Windows CalendarReminderEditorDialog, macOS CalendarReminderEditorSheet): any number of
/// reminders, each an amount, a unit, before or after the start and, for email-capable calendars, the method. Save returns
/// the reminders; Cancel, Escape and a closing owner return null.
/// </summary>
internal sealed class CalendarReminderEditorDialog
{
    private const int ContentWidth = 480;

    private readonly CalendarReminderEditorViewModel _editor;
    private readonly FormDialog _form = new(Translator.CalendarReminderEditor_Title, Translator.CalendarReminderEditor_Description, Translator.Buttons_Save);
    private readonly Gtk.ListBox _rows = Gtk.ListBox.New();
    private readonly Gtk.Label _empty = Gtk.Label.New(Translator.CalendarReminderEditor_Empty);
    private readonly List<IDisposable> _rowSubscriptions = [];
    private bool _closed;

    private CalendarReminderEditorDialog(List<CalendarReminderDraft> reminders, bool supportsEmailReminder)
    {
        _editor = new CalendarReminderEditorViewModel(reminders, supportsEmailReminder);
        _form.ContentWidth = ContentWidth;

        _rows.SetSelectionMode(Gtk.SelectionMode.None);
        _rows.AddCssClass("boxed-list");
        WinoA11y.Label(_rows, Translator.CalendarReminderEditor_Title);
        _empty.SetXalign(0);
        _empty.SetWrap(true);
        _empty.AddCssClass("dim-label");

        var add = Gtk.Button.New();
        add.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.Add, Translator.CalendarReminderEditor_Add, 16));
        add.SetHalign(Gtk.Align.Start);
        WinoA11y.Rename(add, Translator.CalendarReminderEditor_Add);

        _form.Add(_rows);
        _form.Add(_empty);
        _form.Add(add);

        _form.Own(GtkSignals.OnClicked(add, () => _editor.AddReminderCommand.Execute(null)));
        NotifyCollectionChangedEventHandler changed = (_, _) => MainLoop.Post(() =>
        {
            if (!_closed) RebuildRows();
        });
        _editor.Rows.CollectionChanged += changed;
        _form.Own(new ActionDisposable(() =>
        {
            _closed = true;
            _editor.Rows.CollectionChanged -= changed;
            ClearRows();
        }));
        RebuildRows();
        _form.SetInitialFocus(add);
    }

#if DEBUG
    private static CalendarReminderEditorDialog? _current;
#endif

    public static async Task<List<CalendarReminderDraft>?> ShowAsync(Gtk.Widget owner, List<CalendarReminderDraft> reminders, bool supportsEmailReminder)
    {
        var dialog = new CalendarReminderEditorDialog(reminders, supportsEmailReminder);
#if DEBUG
        _current = dialog;
#endif
        try
        {
            return await dialog._form.PresentAsync(owner) ? dialog._editor.GetReminders() : null;
        }
        finally
        {
#if DEBUG
            if (ReferenceEquals(_current, dialog)) _current = null;
#endif
        }
    }

    private void RebuildRows()
    {
        ClearRows();
        foreach (var row in _editor.Rows.ToArray()) _rows.Append(RowView(row));
        _rows.SetVisible(_editor.HasRows);
        _empty.SetVisible(!_editor.HasRows);
    }

    private void ClearRows()
    {
        foreach (var subscription in _rowSubscriptions) subscription.Dispose();
        _rowSubscriptions.Clear();
        _rows.RemoveAll();
    }

    private Gtk.ListBoxRow RowView(CalendarReminderEditorRow row)
    {
        var amount = Gtk.SpinButton.NewWithRange(0, 9999, 1);
        amount.SetDigits(0);
        amount.SetNumeric(true);
        amount.SetValue(row.Value);
        amount.SetValign(Gtk.Align.Center);
        WinoA11y.Label(amount, Translator.CalendarReminderEditor_Value);
        _rowSubscriptions.Add(GtkSignals.Connect<Gtk.SpinButton>(h => amount.OnValueChanged += h, h => amount.OnValueChanged -= h, (_, _) =>
        {
            var value = amount.GetValue();
            if (Math.Abs(row.Value - value) > 0.0001) row.Value = value;
            if (Math.Abs(row.Value - amount.GetValue()) > 0.0001) amount.SetValue(row.Value);
        }));

        var parts = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        parts.SetMarginTop(6);
        parts.SetMarginBottom(6);
        parts.SetMarginStart(8);
        parts.SetMarginEnd(6);
        parts.Append(amount);
        parts.Append(Choice(CalendarReminderEditorRow.UnitOptions, row.SelectedUnitIndex, Translator.CalendarReminderEditor_Unit, index => row.SelectedUnitIndex = index));
        parts.Append(Choice(CalendarReminderEditorRow.DirectionOptions, row.SelectedDirectionIndex, Translator.CalendarReminderEditor_Direction, index => row.SelectedDirectionIndex = index));
        if (row.SupportsEmailReminder)
            parts.Append(Choice(CalendarReminderEditorRow.MethodOptions, row.SelectedMethodIndex, Translator.CalendarReminderEditor_Method, index => row.SelectedMethodIndex = index));

        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        parts.Append(spacer);
        var remove = WinoIcons.Button(WinoIconGlyph.Delete, Translator.CalendarReminderEditor_Remove);
        remove.AddCssClass("flat");
        remove.SetValign(Gtk.Align.Center);
        _rowSubscriptions.Add(GtkSignals.OnClicked(remove, () => _editor.RemoveReminderCommand.Execute(row)));
        parts.Append(remove);

        var view = Gtk.ListBoxRow.New();
        view.SetActivatable(false);
        view.SetChild(parts);
        WinoA11y.Label(view, row.Summary);
        return view;
    }

    /// <summary>A drop-down over a fixed option list; it keeps its accessible name, which GTK replaces with the value on every change.</summary>
    private Gtk.DropDown Choice(List<string> options, int selected, string label, Action<int> chosen)
    {
        var dropDown = Gtk.DropDown.NewFromStrings([.. options]);
        dropDown.SetSelected((uint)Math.Clamp(selected, 0, Math.Max(0, options.Count - 1)));
        dropDown.SetValign(Gtk.Align.Center);
        WinoA11y.Rename(dropDown, label);
        _rowSubscriptions.Add(GtkSignals.OnNotify(dropDown, "selected", () =>
        {
            WinoA11y.Rename(dropDown, label);
            var index = dropDown.GetSelected();
            if (index < options.Count) chosen((int)index);
        }));
        return dropDown;
    }

#if DEBUG
    internal static string? DebugDescribe()
    {
        if (_current is not { } dialog) return null;
        var lines = dialog._editor.Rows.Select(static row => string.Create(CultureInfo.InvariantCulture,
            $"{row.Value}|{CalendarReminderEditorRow.UnitOptions[Math.Clamp(row.SelectedUnitIndex, 0, CalendarReminderEditorRow.UnitOptions.Count - 1)]}|{CalendarReminderEditorRow.DirectionOptions[Math.Clamp(row.SelectedDirectionIndex, 0, 1)]}|{(row.SupportsEmailReminder ? CalendarReminderEditorRow.MethodOptions[Math.Clamp(row.SelectedMethodIndex, 0, 1)] : "-")}|{row.Summary}"));
        return $"rows={dialog._editor.Rows.Count} shown={CountRows(dialog._rows)} empty={dialog._empty.GetVisible()} email={dialog._editor.SupportsEmailReminder}\n{string.Join('\n', lines)}";
    }

    /// <summary><c>add</c> | <c>remove i</c> | <c>set i value unitIndex directionIndex [methodIndex]</c> | <c>save</c> | <c>cancel</c>; set goes through the row's widgets.</summary>
    internal static bool DebugApply(string verb, string[] args)
    {
        if (_current is not { } dialog) return false;
        switch (verb)
        {
            case "add":
                dialog._editor.AddReminderCommand.Execute(null);
                return true;
            case "remove" when args.Length > 0 && int.TryParse(args[0], out var index) && index >= 0 && index < dialog._editor.Rows.Count:
                dialog._editor.RemoveReminderCommand.Execute(dialog._editor.Rows[index]);
                return true;
            case "set" when args.Length >= 4 && int.TryParse(args[0], out var row) && dialog._rows.GetRowAtIndex(row) is { } view && view.GetChild() is Gtk.Box parts:
            {
                var values = args[1..];
                var position = 0;
                for (var child = parts.GetFirstChild(); child is not null && position < values.Length; child = child.GetNextSibling())
                {
                    if (child is Gtk.SpinButton spin) spin.SetValue(double.Parse(values[position++], CultureInfo.InvariantCulture));
                    else if (child is Gtk.DropDown dropDown) dropDown.SetSelected(uint.Parse(values[position++], CultureInfo.InvariantCulture));
                }
                return true;
            }
            case "save":
                dialog._form.DebugConfirm();
                return true;
            case "cancel":
                dialog._form.Close(false);
                return true;
            default:
                return false;
        }
    }

    private static int CountRows(Gtk.ListBox list)
    {
        var count = 0;
        while (list.GetRowAtIndex(count) is not null) count++;
        return count;
    }
#endif
}
