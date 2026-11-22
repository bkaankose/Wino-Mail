using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Tasks;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The forms behind the task drawer's reminder, repeat and category rows (macOS TaskFieldSheets, Windows reminder
/// picker, repeat editor and category box). Each returns null when the user cancels.
/// </summary>
internal static class TaskFieldDialogs
{
    private static readonly TaskRecurrenceFrequency[] Units =
        [TaskRecurrenceFrequency.Daily, TaskRecurrenceFrequency.Weekly, TaskRecurrenceFrequency.Monthly, TaskRecurrenceFrequency.Yearly];

    /// <summary>The local date and time of a reminder picked by the user.</summary>
    public static async Task<DateTime?> PickReminderAsync(Gtk.Widget owner, DateTime initialLocal, Func<TimeSpan, string>? timeFormatter)
    {
        var form = new FormDialog(Translator.ToDoPage_ReminderPickDateTime, null, Translator.Buttons_Save);
        var calendar = Gtk.Calendar.New();
        // Day 1 first: moving 31 January to February must never pass through an invalid date. The month is 0-based.
        calendar.SetDay(1);
        calendar.SetYear(initialLocal.Year);
        calendar.SetMonth(initialLocal.Month - 1);
        calendar.SetDay(initialLocal.Day);
        WinoA11y.Label(calendar, Translator.ToDoPage_RemindMe);
        var time = new WinoTimePicker(Translator.ToDoPage_RemindMe) { Formatter = timeFormatter };
        time.Time = new TimeSpan(initialLocal.Hour, initialLocal.Minute, 0);
        time.RefreshLabel();
        form.Own(time);
        var timeRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var timeCaption = Gtk.Label.New(Translator.ToDoPage_RemindMe);
        timeCaption.SetXalign(0);
        timeCaption.SetHexpand(true);
        timeRow.Append(timeCaption);
        timeRow.Append(time.Root);
        form.Add(calendar);
        form.Add(timeRow);
        form.SetInitialFocus(calendar);

        DateTime Value()
        {
            var date = calendar.GetDate();
            return new DateTime(date.GetYear(), date.GetMonth(), date.GetDayOfMonth()).Add(time.Time);
        }
#if DEBUG
        Debug = new DebugForm(form, () => $"reminder={Value():yyyy-MM-ddTHH:mm}", args =>
        {
            if (args.Length > 0 && DateTime.TryParse(args[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                calendar.SetDay(1);
                calendar.SetYear(value.Year);
                calendar.SetMonth(value.Month - 1);
                calendar.SetDay(value.Day);
                time.SetFromUser(value.TimeOfDay);
            }
        });
#endif
        try { return await form.PresentAsync(owner) ? Value() : null; }
        finally { ClearDebug(form); }
    }

    /// <summary>The repeat rule the custom editor builds, as the stored RRULE text.</summary>
    public static async Task<string?> PickRepeatAsync(Gtk.Widget owner, string? currentRule, DateTime anchor)
    {
        var current = TaskRecurrenceRule.Parse(currentRule);
        var form = new FormDialog(Translator.ToDoPage_RepeatPresetCustom, null, Translator.Buttons_Save);

        var intervals = new string[30];
        for (var index = 0; index < intervals.Length; index++) intervals[index] = (index + 1).ToString(CultureInfo.CurrentCulture);
        var interval = Gtk.DropDown.NewFromStrings(intervals);
        interval.SetSelected((uint)Math.Clamp((current?.Interval ?? 1) - 1, 0, intervals.Length - 1));
        var unit = Gtk.DropDown.NewFromStrings([Translator.ToDoPage_RepeatUnitDays, Translator.ToDoPage_RepeatUnitWeeks, Translator.ToDoPage_RepeatUnitMonths, Translator.ToDoPage_RepeatUnitYears]);
        var unitIndex = Array.IndexOf(Units, current?.Frequency ?? TaskRecurrenceFrequency.Weekly);
        unit.SetSelected((uint)(unitIndex < 0 ? 1 : unitIndex));
        WinoA11y.Rename(interval, Translator.ToDoPage_RepeatInterval);
        WinoA11y.Rename(unit, Translator.ToDoPage_RepeatInterval);
        var intervalRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var intervalCaption = Gtk.Label.New(Translator.ToDoPage_RepeatInterval);
        intervalCaption.SetXalign(0);
        intervalCaption.SetHexpand(true);
        intervalRow.Append(intervalCaption);
        intervalRow.Append(interval);
        intervalRow.Append(unit);
        form.Add(intervalRow);

        var daysCaption = Gtk.Label.New(Translator.ToDoPage_RepeatOnDays);
        daysCaption.SetXalign(0);
        form.Add(daysCaption);
        var dayRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        dayRow.SetHomogeneous(true);
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var fullNames = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
        var selectedDays = current is { WeekDays.Count: > 0 } ? current.WeekDays : (IReadOnlyList<DayOfWeek>)[anchor.DayOfWeek];
        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var days = new (Gtk.ToggleButton Button, DayOfWeek Day)[7];
        for (var index = 0; index < 7; index++)
        {
            var day = (DayOfWeek)((first + index) % 7);
            var toggle = Gtk.ToggleButton.NewWithLabel(names[(int)day]);
            toggle.SetActive(selectedDays.Contains(day));
            WinoA11y.Rename(toggle, fullNames[(int)day]);
            days[index] = (toggle, day);
            dayRow.Append(toggle);
        }
        form.Add(dayRow);

        void UpdateDays()
        {
            var weekly = unit.GetSelected() == 1;
            daysCaption.SetSensitive(weekly);
            foreach (var (button, _) in days) button.SetSensitive(weekly);
        }
        form.Own(GtkSignals.OnNotify(unit, "selected", UpdateDays));
        UpdateDays();

        string Rule()
        {
            var frequency = Units[Math.Clamp((int)unit.GetSelected(), 0, Units.Length - 1)];
            var step = (int)interval.GetSelected() + 1;
            List<DayOfWeek>? weekDays = null;
            if (frequency == TaskRecurrenceFrequency.Weekly)
            {
                weekDays = [];
                foreach (var (button, day) in days)
                {
                    if (button.GetActive()) weekDays.Add(day);
                }
                if (weekDays.Count == 0) weekDays.Add(anchor.DayOfWeek);
            }
            return new TaskRecurrenceRule(
                frequency,
                step,
                weekDays,
                MonthDay: frequency is TaskRecurrenceFrequency.Monthly or TaskRecurrenceFrequency.Yearly ? anchor.Day : null,
                Month: frequency == TaskRecurrenceFrequency.Yearly ? anchor.Month : null).ToRule();
        }
#if DEBUG
        Debug = new DebugForm(form, () => $"rule={Rule()} interval={interval.GetSelected() + 1} unit={unit.GetSelected()} daysSensitive={days[0].Button.GetSensitive()}", args =>
        {
            if (args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out var step)) interval.SetSelected((uint)Math.Clamp(step - 1, 0, 29));
            if (args.Length > 1 && int.TryParse(args[1], CultureInfo.InvariantCulture, out var unitValue)) unit.SetSelected((uint)Math.Clamp(unitValue, 0, 3));
            if (args.Length > 2)
            {
                var wanted = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var (button, day) in days) button.SetActive(Array.Exists(wanted, code => string.Equals(code, day.ToString()[..2], StringComparison.OrdinalIgnoreCase)));
            }
        });
#endif
        try { return await form.PresentAsync(owner) ? Rule() : null; }
        finally { ClearDebug(form); }
    }

    /// <summary>A category name typed or picked from the suggestions of the other tasks.</summary>
    public static async Task<string?> PickCategoryAsync(Gtk.Widget owner, IReadOnlyList<string> suggestions)
    {
        var form = new FormDialog(Translator.ToDoPage_AddCategory, null, Translator.Buttons_Add);
        var entry = Adw.EntryRow.New();
        entry.SetUseMarkup(false);
        entry.SetTitle(Translator.ToDoPage_Categories);
        entry.SetActivatesDefault(true);
        var group = form.AddGroup();
        group.Add(entry);

        var buttons = new List<(Gtk.Button Button, string Name)>();
        if (suggestions.Count > 0)
        {
            var box = Adw.WrapBox.New();
            box.SetChildSpacing(6);
            box.SetLineSpacing(6);
            foreach (var name in suggestions)
            {
                var button = Gtk.Button.NewWithLabel(name);
                button.AddCssClass("pill");
                button.AddCssClass("small");
                var picked = name;
                form.Own(GtkSignals.OnClicked(button, () =>
                {
                    entry.SetText(picked);
                    entry.SetPosition(-1);
                }));
                buttons.Add((button, name));
                box.Append(button);
            }
            form.Add(box);
        }

        void Filter()
        {
            var text = entry.GetText().Trim();
            foreach (var (button, name) in buttons)
                button.SetVisible(text.Length == 0 || (name.Contains(text, StringComparison.OrdinalIgnoreCase) && !string.Equals(name, text, StringComparison.OrdinalIgnoreCase)));
            form.Refresh();
        }
        form.CanConfirm = () => entry.GetText().Trim().Length > 0;
        form.Own(GtkSignals.OnChanged(entry, Filter));
        form.SetInitialFocus(entry);
#if DEBUG
        Debug = new DebugForm(form, () =>
        {
            var visible = buttons.Where(static pair => pair.Button.GetVisible()).Select(static pair => pair.Name);
            return $"text='{entry.GetText()}' suggestions=[{string.Join(", ", visible)}]";
        }, args =>
        {
            if (args.Length > 0 && args[0] == "pick" && args.Length > 1) buttons.FirstOrDefault(pair => pair.Name == args[1]).Button?.Activate();
            else entry.SetText(string.Join(' ', args));
        });
#endif
        try { return await form.PresentAsync(owner) ? entry.GetText().Trim() : null; }
        finally { ClearDebug(form); }
    }

#if DEBUG
    internal sealed record DebugForm(FormDialog Form, Func<string> Describe, Action<string[]> Set);

    internal static DebugForm? Debug { get; private set; }

    private static void ClearDebug(FormDialog form)
    {
        if (ReferenceEquals(Debug?.Form, form)) Debug = null;
    }
#else
    private static void ClearDebug(FormDialog form)
    {
    }
#endif
}
