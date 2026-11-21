using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AppKit;
using Foundation;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Tasks;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Dialogs;

/// <summary>
/// The sheets behind the task drawer's reminder, repeat and category rows (Windows reminder picker, repeat editor and
/// category box). Each returns null when the user cancels.
/// </summary>
internal static class TaskFieldSheets
{
    /// <summary>The local date and time of a reminder picked by the user.</summary>
    public static async Task<DateTime?> PickReminderAsync(NSWindow parent, DateTime initialLocal)
    {
        var form = new FormSheet(Translator.ToDoPage_ReminderPickDateTime, null, Translator.Buttons_Save);
        var picker = new NSDatePicker
        {
            DatePickerStyle = NSDatePickerStyle.TextFieldAndStepper,
            DatePickerElements = NSDatePickerElementFlags.YearMonthDateDay | NSDatePickerElementFlags.HourMinute,
            Bezeled = true,
            DateValue = (NSDate)DateTime.SpecifyKind(initialLocal, DateTimeKind.Local),
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        form.AddRow(Translator.ToDoPage_RemindMe, picker);
        if (!await form.PresentAsync(parent)) return null;
        return (DateTime)picker.DateValue;
    }

    /// <summary>The repeat rule the custom editor builds, as the stored RRULE text.</summary>
    public static async Task<string?> PickRepeatAsync(NSWindow parent, string? currentRule, DateTime anchor)
    {
        var current = TaskRecurrenceRule.Parse(currentRule);
        var units = new[] { TaskRecurrenceFrequency.Daily, TaskRecurrenceFrequency.Weekly, TaskRecurrenceFrequency.Monthly, TaskRecurrenceFrequency.Yearly };
        var unitTitles = new[] { Translator.ToDoPage_RepeatUnitDays, Translator.ToDoPage_RepeatUnitWeeks, Translator.ToDoPage_RepeatUnitMonths, Translator.ToDoPage_RepeatUnitYears };

        var form = new FormSheet(Translator.ToDoPage_RepeatPresetCustom, null, Translator.Buttons_Save);
        var intervals = Enumerable.Range(1, 30).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToList();
        var interval = form.AddPopUp(Translator.ToDoPage_RepeatInterval, intervals, Math.Clamp((current?.Interval ?? 1) - 1, 0, intervals.Count - 1));
        var unitIndex = Array.IndexOf(units, current?.Frequency ?? TaskRecurrenceFrequency.Weekly);
        var unit = form.AddPopUp(Translator.ToDoPage_RepeatInterval, unitTitles, unitIndex < 0 ? 1 : unitIndex);

        var days = new List<NSButton>();
        var dayRow = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Horizontal, Spacing = 6, TranslatesAutoresizingMaskIntoConstraints = false };
        var names = DateTimeFormatInfo.CurrentInfo.AbbreviatedDayNames;
        var selectedDays = current is { WeekDays.Count: > 0 } ? current.WeekDays.ToList() : new List<DayOfWeek> { anchor.DayOfWeek };
        for (var index = 0; index < 7; index++)
        {
            var day = (DayOfWeek)((index + 1) % 7);
            var box = new NSButton { Title = names[(int)day], State = selectedDays.Contains(day) ? NSCellStateValue.On : NSCellStateValue.Off };
            box.SetButtonType(NSButtonType.Switch);
            days.Add(box);
            dayRow.AddArrangedSubview(box);
        }
        form.AddRow(Translator.ToDoPage_RepeatOnDays, dayRow);

        void UpdateDays()
        {
            var weekly = unit.IndexOfSelectedItem == 1;
            foreach (var box in days) box.Enabled = weekly;
        }
        unit.Activated += (_, _) => UpdateDays();
        UpdateDays();

        if (!await form.PresentAsync(parent)) return null;

        var frequency = units[(int)unit.IndexOfSelectedItem];
        var step = (int)interval.IndexOfSelectedItem + 1;
        IReadOnlyList<DayOfWeek>? weekDays = null;
        if (frequency == TaskRecurrenceFrequency.Weekly)
        {
            var picked = days.Select((box, index) => (box, day: (DayOfWeek)((index + 1) % 7)))
                .Where(pair => pair.box.State == NSCellStateValue.On).Select(pair => pair.day).ToList();
            weekDays = picked.Count > 0 ? picked : new List<DayOfWeek> { anchor.DayOfWeek };
        }

        var rule = new TaskRecurrenceRule(
            frequency,
            step,
            weekDays,
            MonthDay: frequency is TaskRecurrenceFrequency.Monthly or TaskRecurrenceFrequency.Yearly ? anchor.Day : null,
            Month: frequency == TaskRecurrenceFrequency.Yearly ? anchor.Month : null);
        return rule.ToRule();
    }

    /// <summary>A category name typed or picked from the suggestions of the other tasks.</summary>
    public static async Task<string?> PickCategoryAsync(NSWindow parent, IReadOnlyList<string> suggestions)
    {
        var form = new FormSheet(Translator.ToDoPage_AddCategory, null, Translator.Buttons_Add);
        var combo = new NSComboBox
        {
            Completes = true,
            PlaceholderString = Translator.ToDoPage_AddCategory,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        combo.WidthAnchor.ConstraintGreaterThanOrEqualTo(240).Active = true;
        foreach (var suggestion in suggestions) combo.Add(new NSString(suggestion));
        WinoAccessibility.Label(combo, Translator.ToDoPage_Categories);
        form.AddRow(Translator.ToDoPage_Categories, combo);
        if (!await form.PresentAsync(parent)) return null;
        return combo.StringValue?.Trim() ?? string.Empty;
    }
}
