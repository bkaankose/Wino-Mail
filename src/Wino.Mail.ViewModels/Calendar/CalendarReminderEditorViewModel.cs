using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.ViewModels.Calendar;

/// <summary>
/// The state of the reminder editor: any number of reminders, each a value and unit before or after the
/// event start, notified on the device or (where the calendar's server sends one) by email.
/// </summary>
public partial class CalendarReminderEditorViewModel : ObservableObject
{
    public ObservableCollection<CalendarReminderEditorRow> Rows { get; } = [];

    public bool SupportsEmailReminder { get; }

    public bool HasRows => Rows.Count > 0;

    public CalendarReminderEditorViewModel(IEnumerable<CalendarReminderDraft> reminders, bool supportsEmailReminder)
    {
        SupportsEmailReminder = supportsEmailReminder;

        foreach (var reminder in (reminders ?? []).OrderByDescending(reminder => reminder.OffsetSeconds))
            Rows.Add(CalendarReminderEditorRow.FromDraft(reminder, supportsEmailReminder));

        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
    }

    [RelayCommand]
    private void AddReminder()
        => Rows.Add(new CalendarReminderEditorRow(SupportsEmailReminder) { Value = 15, SelectedUnitIndex = (int)CalendarReminderUnit.Minutes });

    [RelayCommand]
    private void RemoveReminder(CalendarReminderEditorRow row)
    {
        if (row != null)
            Rows.Remove(row);
    }

    /// <summary>The edited reminders without duplicates, latest first.</summary>
    public List<CalendarReminderDraft> GetReminders()
        => Rows
            .Select(row => row.ToDraft())
            .Distinct()
            .OrderByDescending(reminder => reminder.OffsetSeconds)
            .ToList();
}

public partial class CalendarReminderEditorRow : ObservableObject
{
    private const int MaximumValue = 9999;

    public static List<string> UnitOptions { get; } =
    [
        Translator.CalendarReminderEditor_UnitMinutes,
        Translator.CalendarReminderEditor_UnitHours,
        Translator.CalendarReminderEditor_UnitDays,
        Translator.CalendarReminderEditor_UnitWeeks
    ];

    public static List<string> DirectionOptions { get; } =
    [
        Translator.CalendarReminderEditor_BeforeStart,
        Translator.CalendarReminderEditor_AfterStart
    ];

    public static List<string> MethodOptions { get; } =
    [
        Translator.CalendarReminderEditor_MethodNotification,
        Translator.CalendarReminderEditor_MethodEmail
    ];

    public bool SupportsEmailReminder { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial double Value { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int SelectedUnitIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int SelectedDirectionIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int SelectedMethodIndex { get; set; }

    public string Summary
    {
        get
        {
            var draft = ToDraft();
            return CalendarReminderFormatter.Format(draft.OffsetSeconds, draft.ReminderType);
        }
    }

    public CalendarReminderEditorRow(bool supportsEmailReminder)
    {
        SupportsEmailReminder = supportsEmailReminder;
    }

    public static CalendarReminderEditorRow FromDraft(CalendarReminderDraft draft, bool supportsEmailReminder)
    {
        var (value, unit) = CalendarReminderFormatter.Decompose(draft.OffsetSeconds);

        return new CalendarReminderEditorRow(supportsEmailReminder || draft.ReminderType == CalendarItemReminderType.Email)
        {
            Value = value,
            SelectedUnitIndex = (int)unit,
            SelectedDirectionIndex = draft.OffsetSeconds < 0 ? 1 : 0,
            SelectedMethodIndex = draft.ReminderType == CalendarItemReminderType.Email ? 1 : 0
        };
    }

    public CalendarReminderDraft ToDraft()
    {
        var value = double.IsNaN(Value) ? 0 : (long)Math.Clamp(Math.Round(Value), 0, MaximumValue);
        var unit = (CalendarReminderUnit)Math.Clamp(SelectedUnitIndex, 0, UnitOptions.Count - 1);
        var seconds = value * CalendarReminderFormatter.GetUnitSeconds(unit);
        var offset = SelectedDirectionIndex == 1 ? -seconds : seconds;
        var reminderType = SupportsEmailReminder && SelectedMethodIndex == 1 ? CalendarItemReminderType.Email : CalendarItemReminderType.Popup;

        return new CalendarReminderDraft(offset, reminderType);
    }
}
