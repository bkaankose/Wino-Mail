#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Services.Assistant;

/// <summary>
/// Event steps: the device reads the events an <see cref="EventQuery"/> describes (calendar ids, title words, resolved
/// attendees, a window) in its own calendars, or the exact events a selection names, and changes them through the
/// request delegator like the calendar page does. A new event always opens in the editor for the user to check and
/// save; it is never created silently.
/// </summary>
public sealed partial class AgentPlanExecutor
{
    private const int UpcomingDays = 365;
    private static readonly TimeSpan SlotStep = TimeSpan.FromMinutes(15);
    private static readonly TimeOnly DefaultDayStart = new(8, 0);
    private static readonly TimeOnly DefaultDayEnd = new(20, 0);

    // ---- targets -------------------------------------------------------------------------------------------------

    private async Task<List<EventTarget>> ResolveEventsAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (run.Events.TryGetValue(step.N, out var cached))
            return cached;

        var series = step.Args is StepArgs.EventChangeArgs { Series: true } && step.Action != AgentAction.FindEvents;
        List<EventTarget> targets;
        switch (step.Target)
        {
            case StepTarget.Events query:
                targets = await FindEventsAsync(query.AccountId, query.Query, step.Action, series, run, ct).ConfigureAwait(false);
                break;

            case StepTarget.EventItems items:
                targets = await LoadEventsAsync(items.Items, run, ct).ConfigureAwait(false);
                break;

            case StepTarget.FromStep { Part: "items" } from:
                targets = run.Events.TryGetValue(from.N, out var source)
                    ? source.ToList()
                    : await LoadEventsAsync(TouchedOf(run, from.N, ItemKind.Event), run, ct).ConfigureAwait(false);
                break;

            case StepTarget.FromStep from:
            {
                // "My meetings with the people of these mails": the people become the attendee filter.
                var people = await PeopleOfStepAsync(run, from.N, from.Part, ct).ConfigureAwait(false);
                targets = people.Count == 0
                    ? []
                    : await FindEventsAsync(null, EventQuery.Empty with { AttendeeAddresses = people.Select(static person => person.Address).ToList(), All = true }, step.Action, series, run, ct).ConfigureAwait(false);
                break;
            }

            default:
                targets = [];
                break;
        }

        if (series)
            targets = await ToSeriesAsync(targets).ConfigureAwait(false);

        run.Events[step.N] = targets;
        return targets;
    }

    private async Task<List<EventTarget>> LoadEventsAsync(IEnumerable<ItemRef> items, PlanRun run, CancellationToken ct)
    {
        var targets = new List<EventTarget>();
        foreach (var item in items.Where(static item => item.Kind == ItemKind.Event))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(item.Id, out var id))
                continue;

            try
            {
                var calendarItem = await calendarService.GetCalendarItemAsync(id).ConfigureAwait(false);
                if (calendarItem is null)
                    continue;

                calendarItem.AssignedCalendar ??= await calendarService.GetAccountCalendarAsync(calendarItem.CalendarId).ConfigureAwait(false);
                var account = await AccountAsync(run, calendarItem.AssignedCalendar?.AccountId ?? item.AccountId).ConfigureAwait(false);
                if (account is null)
                    continue;

                targets.Add(await ToTargetAsync(calendarItem, account, run).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Could not load an event of an assistant step.");
            }
        }

        return targets.DistinctBy(static target => target.Event.Id).ToList();
    }

    /// <summary>
    /// The events a query matches: in the named calendars (every extended calendar of the account(s) otherwise), inside
    /// the window (the coming year when none), by title words and attendees, earliest first, at most the limit.
    /// </summary>
    private async Task<List<EventTarget>> FindEventsAsync(Guid? accountId, EventQuery query, AgentAction action, bool series, PlanRun run, CancellationToken ct)
    {
        var accounts = await AccountsAsync(run).ConfigureAwait(false);
        if (accountId is Guid one)
            accounts = accounts.Where(account => account.Id == one).ToList();

        var calendars = new List<(AccountCalendar Calendar, MailAccount Account)>();
        foreach (var account in accounts)
        {
            var accountCalendars = await calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
            calendars.AddRange(accountCalendars.Where(static calendar => calendar.IsExtended).Select(calendar => (calendar, account)));
        }

        if (query.CalendarIds.Count > 0)
        {
            var wanted = query.CalendarIds.ToHashSet();
            calendars = calendars.Where(entry => wanted.Contains(entry.Calendar.Id)).ToList();
        }

        var now = DateTime.Now;
        var isMoment = query.From is { } moment && query.To is { } end && moment == end;
        var from = query.From ?? now.AddDays(-1);
        var to = query.To ?? (query.From is { } start ? start.AddDays(UpcomingDays) : now.AddDays(UpcomingDays));
        if (isMoment)
        {
            from = from.AddDays(-1);
            to = to.AddDays(1);
        }

        var rows = new List<(CalendarItem Item, MailAccount Account)>();
        foreach (var (calendar, account) in calendars)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var items = await calendarService.GetCalendarEventsAsync(calendar, new TimeRange(from, to)).ConfigureAwait(false) ?? [];
                rows.AddRange(items.Where(static item => !item.IsHidden).Select(item => (item, account)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Could not read the events of a calendar for an assistant step.");
            }
        }

        rows = rows.DistinctBy(static row => row.Item.Id).ToList();
        if (query.From is null && query.To is null)
            rows = rows.Where(row => row.Item.LocalEndDate > now).ToList();
        else if (isMoment)
            rows = rows.Where(row => row.Item.LocalStartDate == query.From || (row.Item.LocalStartDate <= query.From && query.From < row.Item.LocalEndDate)).ToList();
        else
            rows = rows.Where(row => row.Item.LocalStartDate < to && row.Item.LocalEndDate > from).ToList();

        if (!string.IsNullOrWhiteSpace(query.TitleWords))
        {
            // Either may hold the other: "dentist" for "Dentist appointment", "dentist appointment" for "Dentist".
            var title = query.TitleWords.Trim();
            rows = rows.Where(row => Fits(row.Item.Title, title) || Fits(title, row.Item.Title) || FitsWords(title, row.Item.Title)).ToList();
        }

        if (query.AttendeeAddresses.Count > 0 || !string.IsNullOrWhiteSpace(query.AttendeeWords))
        {
            var matching = new List<(CalendarItem Item, MailAccount Account)>();
            foreach (var row in rows)
            {
                var attendees = await GetAttendeesAsync(row.Item, run).ConfigureAwait(false);
                var addresses = attendees.Select(static attendee => attendee.Email).Append(row.Item.OrganizerEmail);
                var names = attendees.Select(static attendee => attendee.Name).Append(row.Item.OrganizerDisplayName).Concat(addresses);
                if (query.AttendeeAddresses.Any(wanted => addresses.Any(address => SameAddress(address, wanted)))
                    || (!string.IsNullOrWhiteSpace(query.AttendeeWords) && names.Any(name => FitsWords(query.AttendeeWords, name))))
                    matching.Add(row);
            }

            rows = matching;
        }

        rows = rows.OrderBy(static row => row.Item.LocalStartDate).ToList();
        if (series)
            rows = rows.DistinctBy(static row => row.Item.RecurringCalendarItemId ?? row.Item.Id).ToList();

        // "Join my next meeting" means the next one there is a link to join.
        if (action == AgentAction.JoinMeeting && rows.Exists(static row => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(row.Item, out _)))
            rows = rows.Where(static row => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(row.Item, out _)).ToList();

        if (query.Limit > 0)
            rows = rows.Take(query.Limit).ToList();

        var targets = new List<EventTarget>();
        foreach (var (item, account) in rows)
            targets.Add(await ToTargetAsync(item, account, run).ConfigureAwait(false));

        return targets;
    }

    private async Task<EventTarget> ToTargetAsync(CalendarItem item, MailAccount account, PlanRun run)
    {
        var attendees = await GetAttendeesAsync(item, run).ConfigureAwait(false);
        var isOrganizer = string.IsNullOrWhiteSpace(item.OrganizerEmail) || SameAddress(item.OrganizerEmail, account.Address);
        var others = attendees.Count(attendee => !SameAddress(attendee.Email, account.Address));
        return new EventTarget(item, account, isOrganizer, others);
    }

    private async Task<List<CalendarEventAttendee>> GetAttendeesAsync(CalendarItem item, PlanRun run)
    {
        if (run.Attendees.TryGetValue(item.Id, out var cached))
            return cached;

        List<CalendarEventAttendee> attendees;
        try
        {
            attendees = await calendarService.GetAttendeesAsync(item.EventTrackingId).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the attendees of an event for an assistant step.");
            attendees = [];
        }

        run.Attendees[item.Id] = attendees;
        return attendees;
    }

    /// <summary>The series events stand for, for a change meant for the whole series; single events stay as they are.</summary>
    private async Task<List<EventTarget>> ToSeriesAsync(IReadOnlyList<EventTarget> targets)
    {
        var result = new List<EventTarget>();
        foreach (var target in targets)
        {
            if (!target.Event.IsRecurringEvent)
            {
                result.Add(target);
                continue;
            }

            var series = await calendarService.GetCalendarItemTargetAsync(new CalendarItemTarget(target.Event, CalendarEventTargetType.Series)).ConfigureAwait(false);
            result.Add(series is null ? target : target with { Event = series });
        }

        return result.DistinctBy(static target => target.Event.Id).ToList();
    }

    /// <summary>
    /// Drops what an action cannot touch: events in read-only calendars for a change, events the user organizes for an
    /// answer (there is no invitation to answer), and events without a link for a join.
    /// </summary>
    private static List<EventTarget> FilterForAction(AgentAction action, IReadOnlyList<EventTarget> targets, out string? issue)
    {
        issue = null;
        var result = targets.ToList();

        if (action is AgentAction.DeleteEvent or AgentAction.UpdateEvent or AgentAction.SetEventShowAs or AgentAction.RespondToEvent or AgentAction.SetEventReminder)
        {
            var writable = result.Where(static target => target.Event.AssignedCalendar?.IsReadOnly != true).ToList();
            if (writable.Count == 0 && result.Count > 0)
                issue = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ReadOnlyCalendar, EventTitle(result[0].Event));
            result = writable;
        }

        if (action == AgentAction.RespondToEvent && result.Count > 0)
        {
            var invited = result.Where(static target => !target.IsOrganizer).ToList();
            if (invited.Count == 0)
                issue = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_OwnEvent, EventTitle(result[0].Event));
            result = invited;
        }

        if (action == AgentAction.JoinMeeting && result.Count > 0)
        {
            var online = result.Where(static target => CalendarJoinLinkResolver.TryGetEffectiveJoinUri(target.Event, out _)).ToList();
            if (online.Count == 0)
                issue = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoJoinLink, EventTitle(result[0].Event));
            result = online;
        }

        return result;
    }

    private static ItemRef Ref(EventTarget target)
        => new(ItemKind.Event, target.Account.Id, target.Event.Id.ToString());

    private static List<ItemRef> Refs(IEnumerable<EventTarget> targets) => targets.Select(Ref).ToList();

    private static string EventTitle(CalendarItem item)
        => string.IsNullOrWhiteSpace(item.Title) ? Translator.VoiceResult_NoSubject : Plain(item.Title);

    private static string When(CalendarItem item)
        => item.IsAllDayEvent
            ? item.LocalStartDate.ToString("D", CultureInfo.CurrentCulture)
            : item.LocalStartDate.ToString("f", CultureInfo.CurrentCulture);

    private static string EventName(IReadOnlyList<EventTarget> targets)
        => targets.Count == 1 ? EventTitle(targets[0].Event) : EventsText(targets.Count);

    private static string ShowAsName(CalendarItemShowAs showAs) => showAs switch
    {
        CalendarItemShowAs.Free => Translator.VoiceStep_ShowAs_Free,
        CalendarItemShowAs.Tentative => Translator.VoiceStep_ShowAs_Tentative,
        CalendarItemShowAs.OutOfOffice => Translator.VoiceStep_ShowAs_OutOfOffice,
        CalendarItemShowAs.WorkingElsewhere => Translator.VoiceStep_ShowAs_WorkingElsewhere,
        _ => Translator.VoiceStep_ShowAs_Busy
    };

    /// <summary>The show-as the classifier named: its enum name, or the plain words a user says for it.</summary>
    private static CalendarItemShowAs? ParseShowAs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Enum.TryParse<CalendarItemShowAs>(value.Replace(" ", string.Empty), ignoreCase: true, out var parsed))
            return parsed;

        return value.Trim().ToLowerInvariant() switch
        {
            "oof" or "away" or "out of office" or "outofoffice" => CalendarItemShowAs.OutOfOffice,
            "working elsewhere" or "remote" or "elsewhere" => CalendarItemShowAs.WorkingElsewhere,
            "maybe" or "tentative" => CalendarItemShowAs.Tentative,
            "free" or "available" => CalendarItemShowAs.Free,
            "busy" => CalendarItemShowAs.Busy,
            _ => null
        };
    }

    // ---- runners -------------------------------------------------------------------------------------------------

    private async Task<StepOutcome> RunEventStepAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var found = await ResolveEventsAsync(step, run, ct).ConfigureAwait(false);
        if (found.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoEvents);

        var targets = FilterForAction(step.Action, found, out var issue);
        if (targets.Count == 0)
            return Failed(step, issue ?? Translator.VoiceResult_NoEventsLeft);

        var change = step.Args as StepArgs.EventChangeArgs;
        switch (step.Action)
        {
            case AgentAction.FindEvents:
            {
                // Opening an event shows its day; a timed event is the one the user is after, not a holiday that spans it.
                var shown = targets.OrderBy(static target => target.Event.IsAllDayEvent).ThenBy(static target => target.Event.StartDate).First().Event;
                if (Host is { } host)
                    await host.OpenEventAsync(shown).ConfigureAwait(false);

                if (targets.Count == 1)
                    return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventOpened, EventTitle(shown), When(shown)));

                var names = string.Join(", ", targets.Take(ListedItems).Select(static target => $"{EventTitle(target.Event)} ({When(target.Event)})"));
                if (targets.Count > ListedItems)
                    names += string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_AndMore, targets.Count - ListedItems);
                return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TasksFound, EventsText(targets.Count), names));
            }

            case AgentAction.DeleteEvent:
                foreach (var target in targets)
                    await requestDelegator.ExecuteAsync(new CalendarOperationPreparationRequest(CalendarSynchronizerOperation.DeleteEvent, target.Event)).ConfigureAwait(false);

                return Done(step, Refs(targets), targets.Count == 1
                    ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventDeleted, EventTitle(targets[0].Event))
                    : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, EventsText(targets.Count)));

            case AgentAction.UpdateEvent:
                return await UpdateEventsAsync(step, change, targets, run, ct).ConfigureAwait(false);

            case AgentAction.SetEventShowAs:
            {
                if (ParseShowAs(change?.ShowAs) is not { } showAs)
                    return Failed(step, Translator.VoiceIssue_Missing_Change);

                foreach (var target in targets)
                    await SaveEventAsync(target, CalendarSynchronizerOperation.UpdateEvent, item => item.ShowAs = showAs).ConfigureAwait(false);

                return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventShowAs, EventName(targets), ShowAsName(showAs)));
            }

            case AgentAction.RespondToEvent:
            {
                var response = change?.Response?.Trim().ToLowerInvariant() ?? "accept";
                var operation = response switch
                {
                    "decline" or "no" => CalendarSynchronizerOperation.DeclineEvent,
                    "tentative" or "maybe" => CalendarSynchronizerOperation.TentativeEvent,
                    _ => CalendarSynchronizerOperation.AcceptEvent
                };

                var message = change?.Message?.Trim();
                foreach (var target in targets)
                {
                    var request = string.IsNullOrEmpty(message)
                        ? new CalendarOperationPreparationRequest(operation, target.Event)
                        : new CalendarOperationPreparationRequest(operation, target.Event, ResponseMessage: message);
                    await requestDelegator.ExecuteAsync(request).ConfigureAwait(false);
                }

                var template = operation switch
                {
                    CalendarSynchronizerOperation.DeclineEvent => Translator.VoiceResult_EventDeclined,
                    CalendarSynchronizerOperation.TentativeEvent => Translator.VoiceResult_EventTentative,
                    _ => Translator.VoiceResult_EventAccepted
                };
                return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, template, EventName(targets)));
            }

            case AgentAction.SetEventReminder:
            {
                if (change?.ReminderMinutes is not { } minutes || minutes < 0)
                    return Failed(step, Translator.VoiceIssue_Missing_Change);

                foreach (var target in targets)
                {
                    var reminders = new List<Reminder>
                    {
                        new() { Id = Guid.NewGuid(), CalendarItemId = target.Event.Id, DurationInSeconds = minutes * 60L, ReminderType = CalendarItemReminderType.Popup }
                    };
                    await SaveEventAsync(target, CalendarSynchronizerOperation.UpdateEvent, static _ => { }, reminders).ConfigureAwait(false);
                }

                return Done(step, Refs(targets), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ReminderSet, EventName(targets), MinutesText(minutes)));
            }

            case AgentAction.JoinMeeting:
            {
                var meeting = targets[0];
                if (!CalendarJoinLinkResolver.TryGetEffectiveJoinUri(meeting.Event, out var uri) || uri is null)
                    return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoJoinLink, EventTitle(meeting.Event)));
                if (services.GetService<IExternalLauncher>() is not { } launcher)
                    return Failed(step, Translator.VoiceResult_LaunchUnavailable);

                var launched = await launcher.LaunchUriAsync(uri, ct).ConfigureAwait(false);
                return launched.IsSuccess
                    ? Done(step, [Ref(meeting)], string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Joined, EventTitle(meeting.Event)))
                    : Failed(step, launched.ErrorMessage ?? Translator.VoiceResult_LaunchUnavailable);
            }
        }

        return Failed(step, Translator.VoiceIssue_Missing_Change);
    }

    private async Task<StepOutcome> UpdateEventsAsync(ResolvedStep step, StepArgs.EventChangeArgs? change, IReadOnlyList<EventTarget> targets, PlanRun run, CancellationToken ct)
    {
        if (change is null)
            return Failed(step, Translator.VoiceIssue_Missing_Change);

        var timeChanged = change.NewDate is not null || change.NewStart is not null || change.ShiftMinutes is not null || change.DurationMinutes is not null || change.Slot is not null;
        var detailsChanged = !string.IsNullOrWhiteSpace(change.NewTitle) || !string.IsNullOrWhiteSpace(change.NewLocation) || ParseShowAs(change.ShowAs) is not null;
        if (!timeChanged && !detailsChanged)
            return Failed(step, Translator.VoiceIssue_Missing_Change);

        DateTime? lastStart = null;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var current = target.Event;
            var start = current.LocalStartDate;
            var length = current.LocalEndDate - current.LocalStartDate;
            var isAllDay = current.IsAllDayEvent;

            if (change.NewDate is { } day)
                start = day.Date + start.TimeOfDay;

            if (change.NewStart is { } time)
            {
                // The new start is a wall-clock moment; with a new day as well, only its time of day counts.
                start = change.NewDate is { } onDay ? onDay.Date + time.TimeOfDay : DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
                if (isAllDay)
                {
                    // A time for an all-day event makes it a timed one, an hour long unless the user said otherwise.
                    isAllDay = false;
                    length = TimeSpan.FromHours(1);
                }
            }

            if (change.ShiftMinutes is { } shift)
            {
                start = start.AddMinutes(shift);
                isAllDay = isAllDay && shift % (24 * 60) == 0;
            }

            if (change.DurationMinutes is { } minutes && minutes > 0)
            {
                length = TimeSpan.FromMinutes(minutes);
                isAllDay = false;
            }

            if (change.Slot is { } slot)
            {
                if (slot.Minutes > 0)
                    length = TimeSpan.FromMinutes(slot.Minutes);
                else if (isAllDay)
                    length = TimeSpan.FromHours(1);

                var free = slot.Suggested ?? await FindFreeSlotAsync(slot, length, current.Id, run).ConfigureAwait(false);
                if (free is not { } at)
                    return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoFreeSlot, EventTitle(current)));

                start = at;
                isAllDay = false;
            }

            var newStart = start;
            var newLength = isAllDay ? TimeSpan.FromDays(Math.Max(1, Math.Round(length.TotalDays))) : length;
            var showAs = ParseShowAs(change.ShowAs);
            await SaveEventAsync(target, timeChanged && !detailsChanged ? CalendarSynchronizerOperation.ChangeStartAndEndDate : CalendarSynchronizerOperation.UpdateEvent, item =>
            {
                item.StartDate = isAllDay ? newStart.Date : newStart.ToTimeZoneFromLocal(item.StartTimeZone);
                item.DurationInSeconds = newLength.TotalSeconds;
                if (!string.IsNullOrWhiteSpace(change.NewTitle))
                    item.Title = change.NewTitle.Trim();
                if (!string.IsNullOrWhiteSpace(change.NewLocation))
                    item.Location = change.NewLocation.Trim();
                if (showAs is { } value)
                    item.ShowAs = value;
            }).ConfigureAwait(false);

            lastStart = newStart;
        }

        return Done(step, Refs(targets), timeChanged && lastStart is { } moved && targets.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventMoved, EventName(targets), moved.ToString("f", CultureInfo.CurrentCulture))
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventUpdated, EventName(targets)));
    }

    /// <summary>
    /// Changes an event the way the calendar page does: the stored copy is updated first, then the request carries the
    /// original so the synchronizer can send only what changed and roll back on failure.
    /// </summary>
    private async Task SaveEventAsync(EventTarget target, CalendarSynchronizerOperation operation, Action<CalendarItem> change, List<Reminder>? newReminders = null)
    {
        var item = await calendarService.GetCalendarItemAsync(target.Event.Id).ConfigureAwait(false) ?? target.Event;
        var original = await calendarService.GetCalendarItemAsync(target.Event.Id).ConfigureAwait(false) ?? target.Event;
        item.AssignedCalendar ??= target.Event.AssignedCalendar;
        original.AssignedCalendar ??= target.Event.AssignedCalendar;

        var attendees = await calendarService.GetAttendeesAsync(item.EventTrackingId).ConfigureAwait(false) ?? [];
        var originalAttendees = await calendarService.GetAttendeesAsync(item.EventTrackingId).ConfigureAwait(false) ?? [];
        var reminders = await calendarService.GetRemindersAsync(item.Id).ConfigureAwait(false) ?? [];

        change(item);
        await calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
        if (newReminders is not null)
            await calendarService.SaveRemindersAsync(item.Id, newReminders).ConfigureAwait(false);

        await requestDelegator.ExecuteAsync(new CalendarOperationPreparationRequest(
            operation,
            item,
            attendees,
            OriginalItem: original,
            OriginalAttendees: originalAttendees,
            Reminders: newReminders ?? reminders,
            OriginalReminders: reminders)).ConfigureAwait(false);
    }

    // ---- creation ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A new event opens in the editor with everything the plan resolved filled in; the user checks it and saves it.
    /// An event with attendees sends invitations, and a time the classifier assumed may be wrong, so nothing is
    /// created silently.
    /// </summary>
    private async Task<StepOutcome> CreateEventAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (step.Args is not StepArgs.EventArgs details)
            return Failed(step, Translator.VoiceIssue_MissingDetails);
        if (string.IsNullOrWhiteSpace(details.Title))
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Title));
        if (Host is not { } host)
            return Failed(step, Translator.VoiceResult_LaunchUnavailable);

        var calendar = await ResolveCalendarAsync(details.CalendarId, run).ConfigureAwait(false);
        if (calendar is null)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoCalendar, Translator.VoiceBubble_AllAccounts));

        var now = DateTime.Now;
        DateTime start;
        DateTime end;
        if (details.Slot is { } slot && details.Start is null)
        {
            // "Find a free hour tomorrow afternoon": the runtime suggested a time from the busy intervals, or the device picks one.
            var length = TimeSpan.FromMinutes(slot.Minutes > 0 ? slot.Minutes : 60);
            var free = slot.Suggested ?? await FindFreeSlotAsync(slot, length, null, run).ConfigureAwait(false);
            if (free is not { } at)
                return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoFreeSlot, details.Title.Trim()));

            start = at;
            end = at + length;
        }
        else
        {
            start = DateTime.SpecifyKind(details.Start ?? RoundUp(now.AddMinutes(30)), DateTimeKind.Unspecified);
            end = DateTime.SpecifyKind(details.End ?? start.AddHours(1), DateTimeKind.Unspecified);
        }

        if (details.AllDay)
        {
            start = start.Date;
            end = end.Date <= start ? start.AddDays(1) : end.Date;
        }
        else if (end <= start)
        {
            end = start.AddMinutes(30);
        }

        var attendees = details.Attendees.ToList();
        if (attendees.Count == 0 && step.Target is StepTarget.FromStep from)
            attendees = await PeopleOfStepAsync(run, from.N, from.Part, ct).ConfigureAwait(false);

        await host.OpenEventEditorAsync(new CalendarEventComposeNavigationArgs
        {
            HasPendingInput = true,
            SelectedCalendarId = calendar.Id,
            Title = details.Title.Trim(),
            Location = details.Location?.Trim() ?? string.Empty,
            IsAllDay = details.AllDay,
            StartDate = start,
            EndDate = end,
            NotesHtml = string.IsNullOrWhiteSpace(details.Notes) ? string.Empty : WebUtility.HtmlEncode(details.Notes.Trim()),
            ShowAs = ParseShowAs(details.ShowAs),
            Attendees = attendees
                .Where(static person => !string.IsNullOrWhiteSpace(person.Address))
                .Select(static person => new CalendarEventAttendeeDraft(person.Name ?? person.Address, person.Address))
                .ToList()
        }).ConfigureAwait(false);

        return Done(step, [ItemRef.Calendar(calendar.AccountId, calendar.Id)], string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventReview, Plain(details.Title)));
    }

    /// <summary>The named calendar when it can be written to, else the primary writable calendar of an account with calendar access.</summary>
    private async Task<AccountCalendar?> ResolveCalendarAsync(Guid? calendarId, PlanRun run)
    {
        if (calendarId is Guid id && await calendarService.GetAccountCalendarAsync(id).ConfigureAwait(false) is { IsReadOnly: false } named)
            return named;

        foreach (var account in (await AccountsAsync(run).ConfigureAwait(false)).Where(static account => account.IsCalendarAccessGranted && account.IsCalendarAccessEnabled))
        {
            var calendars = await calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
            var writable = calendars.FirstOrDefault(static calendar => calendar.IsPrimary && !calendar.IsReadOnly)
                ?? calendars.FirstOrDefault(static calendar => !calendar.IsReadOnly);
            if (writable is not null)
                return writable;
        }

        return null;
    }

    /// <summary>
    /// The start of a free slot across every calendar on this device, when the runtime did not suggest one. Free events
    /// and all-day events that don't show as out of office leave the time free; <paramref name="excludedEventId"/> is the
    /// event being moved. Starts snap to quarter hours and never fall in the past.
    /// </summary>
    private async Task<DateTime?> FindFreeSlotAsync(SlotRequest slot, TimeSpan length, Guid? excludedEventId, PlanRun run)
    {
        if (length <= TimeSpan.Zero || slot.To < slot.From)
            return null;

        var period = new TimeRange(slot.From.Date, slot.To.Date.AddDays(1));
        var busy = new List<(DateTime Start, DateTime End)>();
        foreach (var account in (await AccountsAsync(run).ConfigureAwait(false)).Where(static account => account.IsCalendarAccessGranted && account.IsCalendarAccessEnabled))
        {
            foreach (var calendar in await calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [])
            {
                var items = await calendarService.GetCalendarEventsAsync(calendar, period).ConfigureAwait(false) ?? [];
                busy.AddRange(items
                    .Where(item => !item.IsHidden && item.Id != excludedEventId && item.ShowAs != CalendarItemShowAs.Free
                        && (!item.IsAllDayEvent || item.ShowAs == CalendarItemShowAs.OutOfOffice))
                    .Select(static item => (item.LocalStartDate, item.LocalEndDate)));
            }
        }

        var hasWork = preferencesService.IsWorkingHoursEnabled && preferencesService.WorkingHourStart >= TimeSpan.Zero
            && preferencesService.WorkingHourEnd > preferencesService.WorkingHourStart && preferencesService.WorkingHourEnd < TimeSpan.FromDays(1);
        var dayStart = slot.WithinWorkingHours && hasWork ? TimeOnly.FromTimeSpan(preferencesService.WorkingHourStart) : DefaultDayStart;
        var dayEnd = slot.WithinWorkingHours && hasWork ? TimeOnly.FromTimeSpan(preferencesService.WorkingHourEnd) : DefaultDayEnd;
        var intervals = busy.Where(static b => b.End > b.Start).OrderBy(static b => b.Start).ToList();
        var now = DateTime.Now;

        var days = Enumerable.Range(0, (slot.To.Date - slot.From.Date).Days + 1).Select(offset => slot.From.Date.AddDays(offset));
        if (slot.Latest)
            days = days.Reverse();

        foreach (var day in days)
        {
            var from = day + dayStart.ToTimeSpan();
            var to = day + dayEnd.ToTimeSpan();
            if (day == slot.From.Date && slot.From > from)
                from = slot.From;
            if (day == slot.To.Date && slot.To.TimeOfDay > TimeSpan.Zero && slot.To < to)
                to = slot.To;
            if (from < now)
                from = RoundUp(now);
            if (to - from < length)
                continue;

            var gaps = Gaps(from, to, intervals).ToList();
            if (slot.Latest)
                gaps.Reverse();

            foreach (var (gapStart, gapEnd) in gaps)
            {
                var start = slot.Latest ? RoundDown(gapEnd - length) : RoundUp(gapStart);
                if (start >= gapStart && start + length <= gapEnd)
                    return start;
            }
        }

        return null;
    }

    private static IEnumerable<(DateTime Start, DateTime End)> Gaps(DateTime from, DateTime to, List<(DateTime Start, DateTime End)> busy)
    {
        var cursor = from;
        foreach (var (start, end) in busy)
        {
            if (end <= cursor)
                continue;
            if (start >= to)
                break;
            if (start > cursor)
                yield return (cursor, start);

            cursor = end > cursor ? end : cursor;
        }

        if (cursor < to)
            yield return (cursor, to);
    }

    private static DateTime RoundUp(DateTime time)
    {
        var ticks = SlotStep.Ticks;
        return new DateTime((time.Ticks + ticks - 1) / ticks * ticks, time.Kind);
    }

    private static DateTime RoundDown(DateTime time)
    {
        var ticks = SlotStep.Ticks;
        return new DateTime(time.Ticks / ticks * ticks, time.Kind);
    }
}
