#if DEBUG
using System.Globalization;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>The composer's state line and the control setters the debug verbs use, so the controls' own push paths run.</summary>
internal sealed partial class CalendarEventComposeView
{
    internal string Describe()
        => $"heading='{_heading.GetText()}' title='{ViewModel.Title}' calendar='{ViewModel.SelectedCalendar?.Name}' calendarSensitive={_calendar.GetSensitive()} " +
           $"start={ViewModel.StartDate:yyyy-MM-dd} {ViewModel.StartTime:hh\\:mm} end={ViewModel.EndDate:yyyy-MM-dd} {ViewModel.EndTime:hh\\:mm} buttons='{_startDate?.Root.GetLabel()} {(_startTime.Root is Gtk.MenuButton s ? s.GetLabel() : string.Empty)}' allDay={ViewModel.IsAllDay} " +
           $"timeRow={_startTime.Root.GetVisible()} zone='{ViewModel.SelectedTimeZoneOption?.Id}' hint='{_localTimeHint.GetText()}' duration='{_duration.GetText()}' rangeError={_rangeError.GetVisible()} " +
           $"showAs={ViewModel.SelectedShowAsOption?.ShowAs} reminders='{ViewModel.ReminderSummary}' private={ViewModel.IsPrivate} online={ViewModel.IsOnlineMeeting}/{_onlineMeeting.GetVisible()} " +
           $"location='{ViewModel.Location}' attachments={ViewModel.Attachments.Count} notes={NotesMode} save='{_save.GetLabel()}' saveSensitive={_save.GetSensitive()} narrow={_narrow} | {DescribeRecurrence()} | {DescribeAttendees()}";

    internal string DebugSet(string field, string value)
    {
        switch (field)
        {
            case "title": _title.SetText(value); break;
            case "location": _location.SetText(value); break;
            case "allday": _allDay.SetActive(value == "on"); break;
            case "private": _private.SetActive(value == "on"); break;
            case "online": _onlineMeeting.SetActive(value == "on"); break;
            case "start" or "end":
                if (!DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)) return "value: yyyy-MM-ddTHH:mm";
                var (date, time) = field == "start" ? (_startDate, _startTime) : (_endDate, _endTime);
                date?.Pick(DateOnly.FromDateTime(moment));
                time.SetFromUser(moment.TimeOfDay);
                break;
            case "showas":
                if (ViewModel.ShowAsOptions.FirstOrDefault(option => option.ShowAs.ToString().Equals(value, StringComparison.OrdinalIgnoreCase)) is { } showAs)
                    _showAs.Root.SetSelected((uint)Array.IndexOf(_showAs.Options, showAs));
                break;
            case "reminder":
                if (int.TryParse(value, out var minutes) && ViewModel.ReminderOptions.FirstOrDefault(option => option.Minutes == minutes) is { } reminder)
                    reminder.IsSelected = !reminder.IsSelected;
                break;
            case "tz":
                var index = Array.FindIndex(_timeZone.Options, option => option.Id == value);
                if (index >= 0) _timeZone.Root.SetSelected((uint)index);
                return $"tzIndex={index}/{_timeZone.Options.Length} selected={_timeZone.Root.GetSelected()} {Describe()}";
            case "calendar":
                var calendars = ViewModel.AvailableCalendars.Where(static calendar => !calendar.IsReadOnly).ToArray();
                if (int.TryParse(value, out var calendarIndex) && calendarIndex >= 0 && calendarIndex < calendars.Length) ViewModel.SelectedCalendar = calendars[calendarIndex];
                break;
            default:
                return "fields: title location allday private online start end showas reminder tz calendar";
        }
        return Describe();
    }
}
#endif
