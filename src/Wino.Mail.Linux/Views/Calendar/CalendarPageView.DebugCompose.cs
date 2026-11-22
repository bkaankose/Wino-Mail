#if DEBUG
using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Debug verbs of the event composer in the cover: <c>compose-new [title]</c>, <c>compose-edit n</c>,
/// <c>compose-set title|location|allday|start|end|showas|reminder|private|online|tz|calendar value</c> (sets the control),
/// <c>compose-repeat preset i|interval n|frequency i|weekday i|end yyyy-MM-dd|none</c>, <c>compose-invite type text|commit|choose i</c>, <c>compose-attendee optional|remove i</c>,
/// <c>compose-notes text</c>, <c>compose-attach path</c>, <c>compose-state</c>, <c>compose-save</c>, <c>compose-discard</c>.
/// Underscores in a value stand for spaces.
/// </summary>
internal sealed partial class CalendarPageView
{
    partial void RegisterComposeDebugVerbs()
    {
        Bindings.Own(CalendarDebugActions.AddVerb("compose-new", async args =>
        {
            var calendar = _shell.AccountCalendarStateService.AllCalendars.FirstOrDefault(static c => !c.IsReadOnly);
            var date = ViewModel.CurrentVisibleRange?.StartDate.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
            ViewModel.SelectedQuickEventDate = null;
            _navigation.Navigate(WinoPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
            {
                SelectedCalendarId = calendar?.Id,
                StartDate = date.AddHours(10),
                EndDate = date.AddHours(10).AddMinutes(30),
                Title = args.Length > 0 ? string.Join(' ', args).Replace('_', ' ') : string.Empty
            });
            return await ComposeStateAfterLoadAsync();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-edit", async args =>
        {
            var items = VisibleItems();
            if (args.Length == 0 || !int.TryParse(args[0], out var index) || index < 0 || index >= items.Count) return $"usage: compose-edit <0..{items.Count - 1}>";
            var item = items[index].CalendarItem;
            _navigation.Navigate(WinoPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
            {
                EditCalendarItemId = item.Id,
                SelectedCalendarId = item.CalendarId,
                NotesHtml = item.Description ?? string.Empty
            });
            return await ComposeStateAfterLoadAsync();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-set", args =>
        {
            if (_cover is not CalendarEventComposeView view || args.Length < 2) return "usage: compose-set <field> <value>";
            return view.DebugSet(args[0], string.Join(' ', args[1..]).Replace('_', ' '));
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-repeat", args =>
        {
            if (_cover is not CalendarEventComposeView view || args.Length < 2) return "usage: compose-repeat preset|interval|frequency|weekday|end <value>";
            switch (args[0])
            {
                case "preset" when int.TryParse(args[1], out var preset): view.DebugSetRepeat(preset); break;
                case "interval" when int.TryParse(args[1], out var interval): view.DebugSetInterval(interval); break;
                case "frequency" when int.TryParse(args[1], out var frequency): view.DebugSetFrequency(frequency); break;
                case "weekday" when int.TryParse(args[1], out var weekday): view.DebugToggleWeekday(weekday); break;
                case "end":
                    view.DebugSetEnd(DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ? end : null);
                    break;
            }
            return view.DescribeRecurrence();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-invite", async args =>
        {
            if (_cover is not CalendarEventComposeView view || args.Length == 0) return "usage: compose-invite type <text>|commit|choose <i>";
            switch (args[0])
            {
                case "type" when args.Length > 1:
                    view.DebugTypeInvite(args[1]);
                    await CalendarPerfProbe.DelayAsync(600);
                    break;
                case "commit":
                    view.DebugCommitInvite();
                    await CalendarPerfProbe.DelayAsync(600);
                    break;
                case "choose" when args.Length > 1 && int.TryParse(args[1], out var index):
                    view.DebugChooseSuggestion(index);
                    await CalendarPerfProbe.DelayAsync(600);
                    break;
            }
            return view.DescribeAttendees();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-attendee", async args =>
        {
            if (_cover is not CalendarEventComposeView view || args.Length < 2 || !int.TryParse(args[1], out var index)) return "usage: compose-attendee optional|remove <i>";
            view.DebugAttendee(args[0], index);
            await CalendarPerfProbe.DelayAsync(300);
            return view.DescribeAttendees();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-notes", async args =>
        {
            if (_cover is not CalendarEventComposeView view) return "no composer";
            await view.DebugSetNotesAsync(string.Join(' ', args).Replace('_', ' '));
            return view.Describe();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-attach", args =>
        {
            if (_cover is not CalendarEventComposeView view || args.Length == 0) return "usage: compose-attach <path>";
            view.AddDroppedFiles([args[0]]);
            return view.Describe();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-reminders", _ => _cover is CalendarEventComposeView view
            ? $"summary='{view.ViewModel.ReminderSummary}'\n" + string.Join('\n', view.ViewModel.ReminderOptions.Select(static o => $"{o.DisplayText}|{o.IsSelected}|custom={o.IsCustom}"))
            : "no composer"));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-customize", _ =>
        {
            if (_cover is not CalendarEventComposeView view) return "no composer";
            if (view.ViewModel.EditRemindersCommand.CanExecute(null)) view.ViewModel.EditRemindersCommand.Execute(null);
            return "editor requested";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-state", _ => _cover is CalendarEventComposeView view ? view.Describe() : $"no composer (cover={_cover?.GetType().Name ?? "-"})"));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-save", async _ =>
        {
            if (_cover is not CalendarEventComposeView view) return "no composer";
            await view.SaveAsync();
            return $"saved={view.ViewModel.LastCreationSucceeded} cover={_cover?.GetType().Name ?? "-"}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("compose-discard", async _ =>
        {
            if (_cover is not CalendarEventComposeView view) return "no composer";
            view.Discard();
            await CalendarPerfProbe.DelayAsync(300);
            return $"cover={_cover?.GetType().Name ?? "-"}";
        }));
    }

    private async Task<string> ComposeStateAfterLoadAsync()
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            await CalendarPerfProbe.DelayAsync(50);
            if (_cover is CalendarEventComposeView view && view.ViewModel.SelectedCalendar is not null && view.ViewModel.ShowAsOptions.Count > 0) return view.Describe();
        }
        return _cover is CalendarEventComposeView late ? late.Describe() : $"no composer (cover={_cover?.GetType().Name ?? "-"})";
    }
}
#endif
