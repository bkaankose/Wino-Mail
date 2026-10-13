#if DEBUG
using System.Globalization;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// Debug bridge commands for the open event composer (the Mac names and outputs):
/// calsave, cal-notes (ready, preserved, the HTML that would be saved), cal-recur custom INTERVAL FREQ [MO,TU…]
/// [until yyyy-mm-dd|never], cal-tz ID (time zone and the local-time hint), cal-invite TEXT (suggestion rows),
/// cal-invite-pick N. Extra: cal-layout (one column or two, and whether the sheet refuses swipe-down).
/// </summary>
public sealed partial class CalendarEventComposePageViewController
{
    private void RegisterComposeDebugCommands()
    {
        UIKitDebugBridge.Register("calsave", async _ =>
        {
            await Dispatcher.ExecuteOnUIThread(() => _saveBinding?.Execute());
            return "ok";
        });
        UIKitDebugBridge.Register("cal-notes", async _ =>
        {
            var html = await GetNotesHtmlAsync();
            bool preserved = ReferenceEquals(html, _originalNotesHtml) || html == _originalNotesHtml;
            return $"ready={_notesReady} failed={_notesFailed} edited={_notesEdited} preserved={preserved} html='{(html.Length > 200 ? html[..200] : html)}'";
        });
        UIKitDebugBridge.Register("cal-recur", async args =>
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
                ? $"summary='{ViewModel.RecurrenceSummary}' card={!_customRecurrence.Hidden} weekdays={(_weekdayBlock is { Hidden: false })} ends={(_recurrenceEndRow is { Hidden: false })} endMode={ButtonTitle(_recurrenceEndMode)} interval={_intervalLabel.Text} frequency={ButtonTitle(_frequencyButton)}"
                : result);
            return result;
        });
        UIKitDebugBridge.Register("cal-tz", async args =>
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
                ? $"selected='{ButtonTitle(_timeZoneButton)}' hint='{_localTimeHint.Text}' hintVisible={!_localTimeHint.Hidden}"
                : result);
            return result;
        });
        UIKitDebugBridge.Register("cal-invite", async args =>
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _invite.Focus();
                _invite.TextField.Text = string.Join(' ', args);
                ScheduleInviteSearch();
            });
            await Task.Delay(800);
            string result = string.Empty;
            await Dispatcher.ExecuteOnUIThread(() => result = $"visible={_inviteSuggestions?.IsVisible} rows={_inviteSuggestions?.Items.Count ?? 0} attendees={ViewModel.Attendees.Count}");
            return result;
        });
        UIKitDebugBridge.Register("cal-invite-pick", async args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0;
            string result = string.Empty;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                var items = _inviteSuggestions?.Items;
                if (items is null || index < 0 || index >= items.Count)
                {
                    result = "picked='' chosen=False";
                    return;
                }
                var suggestion = items[index];
                var title = string.IsNullOrWhiteSpace(suggestion.DisplayName) ? suggestion.Address : suggestion.DisplayName;
                CommitSuggestion(suggestion);
                result = $"picked='{title}' chosen=True";
            });
            await Task.Delay(500);
            return $"{result} attendees={string.Join(", ", ViewModel.Attendees.Select(attendee => attendee.Email))}";
        });
        UIKitDebugBridge.Register("cal-layout", async _ =>
        {
            string result = string.Empty;
            await Dispatcher.ExecuteOnUIThread(() => result = $"wide={_wide} width={View?.Bounds.Width} edited={_edited} modal={ModalInPresentation} save={_saveItem?.Title}/{_saveItem?.Enabled}");
            return result;
        });
    }

    private static string ButtonTitle(UIButton button) => button.Configuration?.Title ?? string.Empty;
}
#endif
