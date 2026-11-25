using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Dialogs;

/// <summary>
/// The event reminders editor as a window sheet (Windows CalendarReminderEditorDialog): any number of reminders, each an
/// amount, a unit, before or after the start and, for email-capable calendars, the method. Save returns the reminders;
/// Cancel and closing the parent return null.
/// </summary>
internal sealed class CalendarReminderEditorSheet
{
    private const double SheetWidth = 480;

    private static WeakReference<CalendarReminderEditorSheet>? _current;

    private readonly CalendarReminderEditorViewModel _editor;
    private readonly Action<Exception> _error;
    private readonly NSWindow _sheet;
    private readonly NSStackView _rows = WinoLayout.VStack(WinoStyle.Space2);
    private readonly NSTextField _empty;
    private readonly NSStackView _stack;
    private readonly NSButton _cancelButton;
    private readonly NSButton _save;
    private bool _presented;

    public CalendarReminderEditorSheet(List<CalendarReminderDraft> reminders, bool supportsEmailReminder, Action<Exception> error)
    {
        _error = error;
        _editor = new CalendarReminderEditorViewModel(reminders, supportsEmailReminder);

        _sheet = new NSWindow(new CGRect(0, 0, SheetWidth, 200), NSWindowStyle.Titled, NSBackingStore.Buffered, false);
        _sheet.ReleaseWhenClosed(false);
        _sheet.Title = Translator.CalendarReminderEditor_Title;

        var heading = WinoStyle.Label(Translator.CalendarReminderEditor_Title, WinoStyle.Heading);
        var description = WinoStyle.Label(Translator.CalendarReminderEditor_Description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        description.PreferredMaxLayoutWidth = (nfloat)(SheetWidth - 40);

        _empty = WinoStyle.Label(Translator.CalendarReminderEditor_Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        _empty.Hidden = _editor.HasRows;

        var add = SettingsBinder.CreateButton(Translator.CalendarReminderEditor_Add, icon: WinoIconGlyph.Add);
        add.Activated += (_, _) => _editor.AddReminderCommand.Execute(null);
        var addRow = WinoLayout.HStack(WinoStyle.Space2, add, WinoLayout.Spacer());

        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        _save = new NSButton { Title = Translator.Buttons_Save, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r" };
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, _save);

        _stack = WinoLayout.VStack(WinoStyle.Space3, heading, description, _rows, _empty, addRow, buttons);
        _stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        _stack.Alignment = NSLayoutAttribute.Leading;
        _cancelButton = cancel;

        _editor.Rows.CollectionChanged += OnRowsChanged;
        RebuildRows();
    }

    public Task<List<CalendarReminderDraft>?> PresentAsync(NSWindow parent)
    {
        if (_presented) throw new InvalidOperationException("A reminder sheet is presented once.");
        _presented = true;
        var completion = new TaskCompletionSource<List<CalendarReminderDraft>?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Finish(List<CalendarReminderDraft>? result)
        {
            if (!completion.TrySetResult(result)) return;
            parent.EndSheet(_sheet);
        }

        _cancelButton.Activated += (_, _) => Finish(null);
        _save.Activated += (_, _) => Finish(_editor.GetReminders());

        WinoLayout.Fill(_stack, _sheet.ContentView!);
        _stack.WidthAnchor.ConstraintEqualTo((nfloat)SheetWidth).Active = true;
        Refit();
        _current = new WeakReference<CalendarReminderEditorSheet>(this);

        NSObject? closing = null;
        closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => Finish(null), parent);
        parent.BeginSheet(_sheet, _ =>
        {
            if (closing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); closing = null; }
            completion.TrySetResult(null);
            _editor.Rows.CollectionChanged -= OnRowsChanged;
            _sheet.Dispose();
        });
        return completion.Task;
    }

#if DEBUG
    internal static string DebugAddRow()
    {
        if (_current is null || !_current.TryGetTarget(out var sheet)) return "no reminder sheet open";
        sheet._editor.AddReminderCommand.Execute(null);
        return $"rows {sheet._editor.Rows.Count}";
    }
#endif

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            RebuildRows();
        }
        catch (Exception exception)
        {
            _error(exception);
        }
    }

    private void RebuildRows()
    {
        foreach (var view in _rows.ArrangedSubviews) { _rows.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var row in _editor.Rows.ToList())
        {
            var view = RowView(row);
            _rows.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(_rows.WidthAnchor).Active = true;
        }
        _empty.Hidden = _editor.HasRows;
        Refit();
    }

    private void Refit()
    {
        _sheet.SetContentSize(_stack.FittingSize);
    }

    private NSView RowView(CalendarReminderEditorRow row)
    {
        var amount = new NSTextField { StringValue = row.Value.ToString(CultureInfo.InvariantCulture), Alignment = NSTextAlignment.Right, TranslatesAutoresizingMaskIntoConstraints = false };
        amount.WidthAnchor.ConstraintEqualTo(64).Active = true;
        WinoAccessibility.Label(amount, Translator.CalendarReminderEditor_Value);
        amount.Changed += (_, _) =>
        {
            if (double.TryParse(amount.StringValue, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value >= 0)
                row.Value = value;
        };

        var unit = Popup(CalendarReminderEditorRow.UnitOptions, row.SelectedUnitIndex, Translator.CalendarReminderEditor_Unit,
            index => row.SelectedUnitIndex = index);
        var direction = Popup(CalendarReminderEditorRow.DirectionOptions, row.SelectedDirectionIndex, Translator.CalendarReminderEditor_Direction,
            index => row.SelectedDirectionIndex = index);

        var parts = new List<NSView> { amount, unit, direction };
        if (row.SupportsEmailReminder)
            parts.Add(Popup(CalendarReminderEditorRow.MethodOptions, row.SelectedMethodIndex, Translator.CalendarReminderEditor_Method,
                index => row.SelectedMethodIndex = index));

        var remove = SettingsBinder.CreateButton(Translator.CalendarReminderEditor_Remove, icon: WinoIconGlyph.Delete);
        remove.Activated += (_, _) => _editor.RemoveReminderCommand.Execute(row);
        parts.Add(remove);

        var line = WinoLayout.HStack(WinoStyle.Space2, parts.ToArray());
        line.Alignment = NSLayoutAttribute.CenterY;
        return line;
    }

    private static NSPopUpButton Popup(IEnumerable<string> items, int selectedIndex, string label, Action<int> selected)
    {
        var popup = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        foreach (var item in items)
            popup.Menu!.AddItem(new NSMenuItem(item ?? string.Empty));
        popup.SelectItem(selectedIndex >= 0 && selectedIndex < popup.ItemCount ? selectedIndex : 0);
        popup.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        WinoAccessibility.Label(popup, label);
        popup.Activated += (_, _) => selected((int)popup.IndexOfSelectedItem);
        return popup;
    }
}
