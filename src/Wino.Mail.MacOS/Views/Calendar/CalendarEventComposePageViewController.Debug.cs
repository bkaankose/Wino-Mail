#if DEBUG
using System.Globalization;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// Debug bridge commands for the open event composer:
/// cal-notes (ready, preserved, the HTML that would be saved), cal-recur custom INTERVAL FREQ [MO,TU…] [until yyyy-mm-dd|never],
/// cal-tz ID (time zone and the local-time hint), cal-invite TEXT (suggestion rows), cal-invite-pick N.
/// </summary>
public sealed partial class CalendarEventComposePageViewController
{
    private void RegisterComposeDebugCommands()
    {
        MacDebugBridge.Register("cal-notes", async _ =>
        {
            var html = await GetNotesHtmlAsync();
            bool preserved = ReferenceEquals(html, _originalNotesHtml) || html == _originalNotesHtml;
            return $"ready={_notesReady} failed={_notesFailed} edited={_notesEdited} preserved={preserved} html='{(html.Length > 200 ? html[..200] : html)}'";
        });
        MacDebugBridge.Register("cal-recur", async args =>
        {
            string result = string.Empty;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (args.Length < 3 || !args[0].Equals("custom", StringComparison.OrdinalIgnoreCase)) { result = "usage: cal-recur custom INTERVAL Daily|Weekly|Monthly|Yearly [MO,TU…] [until yyyy-mm-dd|never]"; return; }
                ViewModel.SelectedRepeatOption = ViewModel.RepeatOptions.First(option => option.Kind == CalendarComposeRepeatKind.Custom);
                ViewModel.SelectedRecurrenceInterval = int.Parse(args[1], CultureInfo.InvariantCulture);
                var frequency = Enum.Parse<CalendarItemRecurrenceFrequency>(args[2], true);
                ViewModel.SelectedRecurrenceFrequencyOption = ViewModel.RecurrenceFrequencyOptions.First(option => option.Frequency == frequency);
                for (int index = 3; index < args.Length; index++)
                {
                    if (args[index].Equals("until", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                    {
                        var date = DateTime.ParseExact(args[++index], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                        ViewModel.RecurrenceEndDate = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
                    }
                    else if (args[index].Equals("never", StringComparison.OrdinalIgnoreCase))
                    {
                        ViewModel.ClearRecurrenceEndDateCommand.Execute(null);
                    }
                    else
                    {
                        var days = args[index].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(day => day.ToUpperInvariant()).ToHashSet();
                        foreach (var option in ViewModel.WeekdayOptions) option.IsSelected = days.Contains(option.RuleValue);
                    }
                }
            });
            await Task.Delay(150);
            await Dispatcher.ExecuteOnUIThread(() => result = string.IsNullOrEmpty(result)
                ? $"summary='{ViewModel.RecurrenceSummary}' card={!_customRecurrence.Hidden} weekdays={!_weekdayRow.Hidden} ends={!_recurrenceEndRow.Hidden} endMode={_recurrenceEndMode.TitleOfSelectedItem} interval={_interval.StringValue} frequency={_frequency.TitleOfSelectedItem}"
                : result);
            return result;
        });
        MacDebugBridge.Register("cal-tz", async args =>
        {
            string result = string.Empty;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                var option = ViewModel.TimeZoneOptions.FirstOrDefault(zone => args.Length > 0 && zone.Id.Equals(args[0], StringComparison.OrdinalIgnoreCase));
                if (option is null) { result = $"unknown zone; {ViewModel.TimeZoneOptions.Count} options, local={TimeZoneInfo.Local.Id}"; return; }
                ViewModel.SelectedTimeZoneOption = option;
            });
            await Task.Delay(150);
            await Dispatcher.ExecuteOnUIThread(() => result = string.IsNullOrEmpty(result)
                ? $"selected='{_timeZone.TitleOfSelectedItem}' hint='{_localTimeHint.StringValue}' hintVisible={!_localTimeHint.Hidden}"
                : result);
            return result;
        });
        MacDebugBridge.Register("cal-invite", async args =>
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                View.Window?.MakeFirstResponder(_invite);
                _invite.StringValue = string.Join(' ', args);
                ScheduleInviteSearch();
            });
            await Task.Delay(800);
            return $"visible={_inviteSuggestions?.IsVisible} attendees={ViewModel.Attendees.Count}";
        });
        MacDebugBridge.Register("cal-invite-pick", async args =>
        {
            int steps = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) + 1 : 1;
            string result = string.Empty;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                for (int step = 0; step < steps; step++) _inviteSuggestions?.Move(1);
                result = $"picked='{_inviteSuggestions?.Highlighted?.Title}' chosen={_inviteSuggestions?.TryChoose()}";
            });
            await Task.Delay(500);
            return $"{result} attendees={string.Join(", ", ViewModel.Attendees.Select(attendee => attendee.Email))}";
        });
    }
}
#endif
